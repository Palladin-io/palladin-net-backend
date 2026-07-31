using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Palladin.Module.Search.Contracts.Commands;
using Palladin.Module.Search.Contracts.ValueObjects;
using Palladin.Module.Search.Features;
using Palladin.Module.Search.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Search;

[Collection<ApiFactoryCollection>]
public sealed class SearchProjectionTests(ApiFactory apiFactory) : TestBase
{
    [Theory]
    [InlineData(SearchItemTypes.Agent)]
    [InlineData(SearchItemTypes.Member)]
    public async Task AdministrativeItem_IsProjectedIdempotently(string type)
    {
        var id = Guid.NewGuid();
        var organizationId = Guid.NewGuid();
        var current = Now() + Duration.FromSeconds(1);
        await IndexAsync(new(organizationId, id, type, "Current", ["search@example.test"], current));
        await IndexAsync(new(organizationId, id, type, "Stale", [], Now()));

        var item = await QueryAsync(ctx => ctx.Items.SingleAsync(
            x => x.OrganizationId == organizationId && x.Id == id));
        item.Name.ShouldBe("Current");
        item.SearchText.ShouldBe("Current search@example.test");
    }

    [Theory]
    [InlineData("vault")]
    [InlineData("entry")]
    public async Task VaultPresentationType_IsRejectedAndNotPersisted(string forbiddenType)
    {
        var organizationId = Guid.NewGuid();
        var id = Guid.NewGuid();
        await Should.ThrowAsync<InvalidOperationException>(() =>
            IndexAsync(new(organizationId, id, forbiddenType, "plaintext secret", [], Now())));

        (await QueryAsync(ctx => ctx.Items.AnyAsync(
            x => x.OrganizationId == organizationId && x.Id == id))).ShouldBeFalse();
    }

    [Fact]
    public async Task SameIdentifier_IsIsolatedAcrossOrganizations_AndRemovalIsTenantFirst()
    {
        var id = Guid.NewGuid();
        var firstOrganization = Guid.NewGuid();
        var secondOrganization = Guid.NewGuid();
        await IndexAsync(new(firstOrganization, id, SearchItemTypes.Member, "First", [], Now()));
        await IndexAsync(new(secondOrganization, id, SearchItemTypes.Member, "Second", [], Now()));

        await apiFactory.ConsumeAsync<RemoveSearchItemConsumer, RemoveSearchItemCommand>(
            new(firstOrganization, id, SearchItemTypes.Member, Now()));

        var rows = await QueryAsync(ctx => ctx.Items.Where(x => x.Id == id).ToListAsync());
        rows.Count.ShouldBe(2);
        rows.Single(x => x.OrganizationId == firstOrganization).IsRemoved.ShouldBeTrue();
        rows.Single(x => x.OrganizationId == secondOrganization).IsRemoved.ShouldBeFalse();
    }

    [Fact]
    public async Task StaleRemoval_DoesNotDeleteNewerProjection()
    {
        var organizationId = Guid.NewGuid();
        var id = Guid.NewGuid();
        var indexedAt = Now() + Duration.FromMinutes(1);
        await IndexAsync(new(organizationId, id, SearchItemTypes.Member, "Current", [], indexedAt));

        await apiFactory.ConsumeAsync<RemoveSearchItemConsumer, RemoveSearchItemCommand>(
            new(organizationId, id, SearchItemTypes.Member, indexedAt - Duration.FromSeconds(1)));
        (await QueryAsync(ctx => ctx.Items.AnyAsync(
            x => x.OrganizationId == organizationId && x.Id == id))).ShouldBeTrue();

        await apiFactory.ConsumeAsync<RemoveSearchItemConsumer, RemoveSearchItemCommand>(
            new(organizationId, id, SearchItemTypes.Member, indexedAt));
        (await QueryAsync(ctx => ctx.Items.SingleAsync(
            x => x.OrganizationId == organizationId && x.Id == id))).IsRemoved.ShouldBeTrue();
    }

    [Fact]
    public async Task RemovalBeforeStaleIndex_PersistsWatermarkAndPreventsResurrection()
    {
        var organizationId = Guid.NewGuid();
        var id = Guid.NewGuid();
        var rawRemovedAt = Now() + Duration.FromMinutes(1);
        var removedAt = Instant.FromUnixTimeTicks(
            rawRemovedAt.ToUnixTimeTicks() - rawRemovedAt.ToUnixTimeTicks() % 10);

        await apiFactory.ConsumeAsync<RemoveSearchItemConsumer, RemoveSearchItemCommand>(
            new(organizationId, id, SearchItemTypes.Member, removedAt));
        await IndexAsync(new(organizationId, id, SearchItemTypes.Member, "Removed", [], Now()));

        var tombstone = await QueryAsync(ctx => ctx.Items.SingleAsync(
            x => x.OrganizationId == organizationId && x.Id == id));
        tombstone.IsRemoved.ShouldBeTrue();
        tombstone.UpdatedAt.ShouldBe(removedAt);
        tombstone.Name.ShouldBeEmpty();
    }

    [Fact]
    public async Task NewerIndex_CanRecreateAnAdministrativeItemAfterRemoval()
    {
        var organizationId = Guid.NewGuid();
        var id = Guid.NewGuid();
        var removedAt = Now();
        await apiFactory.ConsumeAsync<RemoveSearchItemConsumer, RemoveSearchItemCommand>(
            new(organizationId, id, SearchItemTypes.Agent, removedAt));

        await IndexAsync(new(organizationId, id, SearchItemTypes.Agent, "Re-enrolled", [],
            removedAt + Duration.FromSeconds(1)));

        var item = await QueryAsync(ctx => ctx.Items.SingleAsync(
            x => x.OrganizationId == organizationId && x.Id == id));
        item.IsRemoved.ShouldBeFalse();
        item.Name.ShouldBe("Re-enrolled");
    }

    [Fact]
    public async Task ConcurrentIndexAndNewerRemoval_AfterRedelivery_KeepTheRemovalWatermark()
    {
        var organizationId = Guid.NewGuid();
        var indexedAt = Now();
        var itemIds = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var id in itemIds)
        {
            await IndexAsync(new(organizationId, id, SearchItemTypes.Member, "Initial", [], indexedAt));
        }

        foreach (var id in itemIds)
        {
            var index = new IndexSearchItemCommand(
                organizationId, id, SearchItemTypes.Member, "Stale", [],
                indexedAt + Duration.FromSeconds(1));
            var remove = new RemoveSearchItemCommand(
                organizationId, id, SearchItemTypes.Member,
                indexedAt + Duration.FromSeconds(2));

            try
            {
                await Task.WhenAll(
                    IndexAsync(index),
                    apiFactory.ConsumeAsync<RemoveSearchItemConsumer, RemoveSearchItemCommand>(remove));
            }
            catch (DbUpdateConcurrencyException)
            {
                // The production endpoint delegates this transient conflict to MassTransit.
            }

            // Model broker redelivery. Both messages are idempotent, regardless of which write lost the race.
            await IndexAsync(index);
            await apiFactory.ConsumeAsync<RemoveSearchItemConsumer, RemoveSearchItemCommand>(remove);
        }

        var rows = await QueryAsync(ctx => ctx.Items
            .Where(x => x.OrganizationId == organizationId && itemIds.Contains(x.Id))
            .ToListAsync());
        rows.Count.ShouldBe(itemIds.Length);
        rows.ShouldAllBe(item => item.IsRemoved);
    }

    [Fact]
    public async Task SearchText_IsBoundedToFourAdditionalTerms()
    {
        var organizationId = Guid.NewGuid();
        var id = Guid.NewGuid();
        await IndexAsync(new(organizationId, id, SearchItemTypes.Member, " Name ",
            [" one ", "two", "three", "four", "must-not-be-stored"], Now()));

        var text = await QueryAsync(ctx => ctx.Items
            .Where(x => x.OrganizationId == organizationId && x.Id == id)
            .Select(x => x.SearchText).SingleAsync());
        text.ShouldBe("Name one two three four");
    }

    [Fact]
    public async Task OversizedAdministrativeFields_AreDeterministicallyBounded()
    {
        var organizationId = Guid.NewGuid();
        var id = Guid.NewGuid();
        await IndexAsync(new(organizationId, id, SearchItemTypes.Member,
            new string('n', 300), [new string('t', 1200)], Now()));

        var item = await QueryAsync(ctx => ctx.Items.SingleAsync(
            x => x.OrganizationId == organizationId && x.Id == id));
        item.Name.Length.ShouldBe(256);
        item.SearchText.Length.ShouldBe(1024);
    }

    private Task IndexAsync(IndexSearchItemCommand command) =>
        apiFactory.ConsumeAsync<IndexSearchItemConsumer, IndexSearchItemCommand>(command);

    private async Task<T> QueryAsync<T>(Func<SearchDbReadContext, Task<T>> query)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<SearchDbReadContext>());
    }

    private Instant Now() => apiFactory.FakeClock.GetCurrentInstant();
}
