using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NodaTime;
using NSubstitute;
using Palladin.Core.Events;
using Palladin.Core.Types;
using Palladin.Module.Vault.Contracts.Commands;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Sharing;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Mocks;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault;

[Collection<ApiFactoryCollection>]
public sealed class EntrySharingSourceRevocationTests(ApiFactory apiFactory) : TestBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task When_SourceRevocationIsRetriedAcrossPages_Then_EachAffectedShareHasOneDurableOccurrence(bool organizationWide)
    {
        // Given
        var seeded = await SeedAsync();
        var options = Options.Create(new EntrySharingOptions { SourceRevocationBatchSize = 1 });
        for (var attempt = 0; attempt < 2; attempt++)
        {
            // When
            await using var scope = apiFactory.Services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>();
            if (organizationWide)
            {
                await new RevokeOrganizationEntrySharingConsumer(context, options, apiFactory.FakeClock).Consume(
                    apiFactory.MockConsumeContext(new RevokeOrganizationEntrySharingCommand(seeded.OrganizationId, apiFactory.FakeClock.GetCurrentInstant())));
            }
            else
            {
                await new RevokeMemberEntrySharingConsumer(context, options, apiFactory.FakeClock).Consume(
                    apiFactory.MockConsumeContext(new RevokeMemberEntrySharingCommand(seeded.OrganizationId, seeded.SenderId, 1, apiFactory.FakeClock.GetCurrentInstant())));
            }

            // Then
            scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>().ChangeTracker.Entries().ShouldBeEmpty();
        }

        await using var verification = apiFactory.Services.CreateAsyncScope();
        var database = verification.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
        var affected = organizationWide ? seeded.OldShareIds.Append(seeded.NewShareId).Append(seeded.OtherMemberShareId).ToArray() : seeded.OldShareIds;
        var shares = await database.EntryShares.Where(x => affected.Contains(x.Id)).ToListAsync(TestContext.Current.CancellationToken);
        shares.Count.ShouldBe(affected.Length);
        shares.ShouldAllBe(x => x.RevocationReason == EntryShareActivityKind.SourceAccessRemoved
            && x.Ciphertext.Length == 0 && x.Nonce.Length == 0 && x.AccessTokenHash.Length == 0);
        var occurrences = await database.EntryShareActivities.Where(x => affected.Contains(x.ShareId)
            && x.Kind == EntryShareActivityKind.SourceAccessRemoved).ToListAsync(TestContext.Current.CancellationToken);
        occurrences.Count.ShouldBe(affected.Length);
        occurrences.Select(x => x.ShareId).Distinct().Count().ShouldBe(affected.Length);
        (await database.EntryShares.SingleAsync(x => x.Id == seeded.ForeignShareId, TestContext.Current.CancellationToken)).RevokedAt.ShouldBeNull();
        if (!organizationWide)
        {
            (await database.EntryShares.SingleAsync(x => x.Id == seeded.NewShareId, TestContext.Current.CancellationToken)).RevokedAt.ShouldBeNull();
            (await database.EntryShares.SingleAsync(x => x.Id == seeded.OtherMemberShareId, TestContext.Current.CancellationToken)).RevokedAt.ShouldBeNull();
        }
    }

    [Fact]
    public async Task When_PublicationFailsAfterARevocationPage_Then_TheFenceAndJournalSurviveAndRetryFinishes()
    {
        // Given
        var seeded = await SeedAsync();
        var options = Options.Create(new EntrySharingOptions { SourceRevocationBatchSize = 1 });
        var command = new RevokeMemberEntrySharingCommand(seeded.OrganizationId, seeded.SenderId, 1, apiFactory.FakeClock.GetCurrentInstant());
        var publisher = Substitute.For<IEventPublisher>();
        publisher.PublishAsync(Arg.Any<IEvent>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("synthetic publisher interruption")));

        // When
        await using (var failedScope = apiFactory.Services.CreateAsyncScope())
        {
            var database = failedScope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
            var context = new VaultDomainWriteContext(database, [publisher]);
            await Should.ThrowAsync<InvalidOperationException>(() =>
                new RevokeMemberEntrySharingConsumer(context, options, apiFactory.FakeClock).Consume(apiFactory.MockConsumeContext(command)));
        }

        // Then
        await using (var interrupted = apiFactory.Services.CreateAsyncScope())
        {
            var database = interrupted.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
            (await database.EntryShareSenderAuthorities.SingleAsync(x => x.OrganizationId == seeded.OrganizationId
                && x.UserId == seeded.SenderId, TestContext.Current.CancellationToken)).RevokedThroughAuthorizationVersion.ShouldBe(1u);
            (await database.EntryShareActivities.CountAsync(x => seeded.OldShareIds.Contains(x.ShareId)
                && x.Kind == EntryShareActivityKind.SourceAccessRemoved, TestContext.Current.CancellationToken)).ShouldBe(1);
        }
        await using (var retryScope = apiFactory.Services.CreateAsyncScope())
        {
            await new RevokeMemberEntrySharingConsumer(retryScope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>(),
                options, apiFactory.FakeClock).Consume(apiFactory.MockConsumeContext(command));
        }
        await using var verification = apiFactory.Services.CreateAsyncScope();
        var verified = verification.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
        (await verified.EntryShareActivities.CountAsync(x => seeded.OldShareIds.Contains(x.ShareId)
            && x.Kind == EntryShareActivityKind.SourceAccessRemoved, TestContext.Current.CancellationToken)).ShouldBe(seeded.OldShareIds.Length);
    }

    private async Task<SharingSeed> SeedAsync()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var entry = await apiFactory.Services.SeedSyncEntryAsync(organization.Id, vault.Id, user.Id);
        var otherMember = await apiFactory.Services.SeedAdditionalOrganizationMemberAsync(organization.Id);
        var otherVault = await apiFactory.Services.SeedVaultAsync(organization.Id, otherMember.Id);
        var otherEntry = await apiFactory.Services.SeedSyncEntryAsync(organization.Id, otherVault.Id, otherMember.Id);
        var (foreignUser, foreignOrganization, _) = await apiFactory.Services.SeedUserAsync();
        var foreignVault = await apiFactory.Services.SeedVaultAsync(foreignOrganization.Id, foreignUser.Id);
        var foreignEntry = await apiFactory.Services.SeedSyncEntryAsync(foreignOrganization.Id, foreignVault.Id, foreignUser.Id);
        var now = apiFactory.FakeClock.GetCurrentInstant();
        var source = new EntryScope(organization.Id, vault.Id, entry);
        var oldShares = Enumerable.Range(0, 3).Select(_ => Share(source, user.Id, 1)).ToArray();
        var newShare = Share(source, user.Id, 2);
        var otherMemberShare = Share(new EntryScope(organization.Id, otherVault.Id, otherEntry), otherMember.Id, 1);
        var foreign = Share(new EntryScope(foreignOrganization.Id, foreignVault.Id, foreignEntry), foreignUser.Id, 1);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>();
        context.AddRange(oldShares.Append(newShare).Append(otherMemberShare).Append(foreign));
        await context.CommitAsync(TestContext.Current.CancellationToken);
        return new SharingSeed(organization.Id, user.Id, oldShares.Select(x => x.Id).ToArray(), newShare.Id, otherMemberShare.Id, foreign.Id);

        EntryShare Share(EntryScope entryScope, Guid sender, uint version) => EntryShare.Create(Guid.NewGuid(),
            entryScope, new EntryRevision(1), sender, now, now + Duration.FromHours(1), null,
            EntryShareRecipientMode.AnyoneWithLink, null, EntryShareProtection.None, null,
            new byte[32], new byte[24], new byte[16], false, version, now);
    }

    private sealed record SharingSeed(Guid OrganizationId, Guid SenderId, Guid[] OldShareIds, Guid NewShareId, Guid OtherMemberShareId, Guid ForeignShareId);
}
