using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using Palladin.Core.Types;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Purge;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault;

[Collection<ApiFactoryCollection>]
public sealed class EntrySharingCleanupTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_PublishedJournalRetentionElapses_Then_OnlyAcknowledgedOldRowsAreRemovedInBoundedBatches()
    {
        // Given
        Instant oldestPublished;
        await using (var lookupScope = apiFactory.Services.CreateAsyncScope())
        {
            oldestPublished = await lookupScope.ServiceProvider.GetRequiredService<VaultDbReadContext>()
                .Set<EntryShareActivity>().Where(x => x.PublishedAt != null)
                .MinAsync(x => x.PublishedAt, TestContext.Current.CancellationToken)
                ?? apiFactory.FakeClock.GetCurrentInstant();
        }
        var cutoff = oldestPublished - Duration.FromDays(1);
        var now = cutoff + Duration.FromDays(7);
        var clock = new FakeClock(now);
        var source = new EntryScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var activities = Enumerable.Range(0, 5)
            .Select(_ => Share(source, Guid.NewGuid(), now - Duration.FromDays(30)).Activities.Single()).ToArray();
        activities[0].MarkPublished(cutoff - Duration.FromDays(1));
        activities[1].MarkPublished(cutoff);
        activities[2].MarkPublished(cutoff + Duration.FromSeconds(1));
        activities[3].MarkPublished(now);
        await using (var seedScope = apiFactory.Services.CreateAsyncScope())
        {
            var database = seedScope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
            database.EntryShareActivities.AddRange(activities);
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // When
        await CleanupAsync(clock);

        // Then
        (await JournalIdsAsync(source)).ShouldBe(activities.Skip(1).Select(x => x.ShareId), ignoreOrder: true);
        await CleanupAsync(clock);
        await CleanupAsync(clock);
        (await JournalIdsAsync(source)).ShouldBe(activities.Skip(2).Select(x => x.ShareId), ignoreOrder: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task When_SourceIsPhysicallyDeleted_Then_SharingMaterialCascadesButPendingJournalSurvives(bool deleteVault)
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var entryId = await apiFactory.Services.SeedSyncEntryAsync(organization.Id, vault.Id, user.Id);
        var source = new EntryScope(organization.Id, vault.Id, entryId);
        var now = apiFactory.FakeClock.GetCurrentInstant();
        var share = Share(source, user.Id, now);
        var session = share.OpenSession(Guid.NewGuid(), new byte[32], now, Duration.FromMinutes(15));
        var reservation = EntryShareCreationChallenge.Create(source, user.Id, Guid.NewGuid(), now + Duration.FromMinutes(5));
        await using (var seedScope = apiFactory.Services.CreateAsyncScope())
        {
            var database = seedScope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
            database.EntryShares.Add(share);
            database.EntryShareSessions.Add(session);
            database.EntryShareCreationChallenges.Add(reservation);
            database.EntryShareActivities.AddRange(share.Activities);
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // When
        if (deleteVault)
        {
            var client = apiFactory.CreateAuthenticatedClient(user);
            (await client.DeleteAsync($"api/vaults/{vault.Id}", TestContext.Current.CancellationToken))
                .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }
        else
        {
            await using var scope = apiFactory.Services.CreateAsyncScope();
            (await scope.ServiceProvider.GetRequiredService<EntryPurgeService>()
                .PurgeAsync(source, user.Id, null, false, false, TestContext.Current.CancellationToken))
                .ShouldBe(EntryPurgeResult.Purged);
        }

        // Then
        await using var verificationScope = apiFactory.Services.CreateAsyncScope();
        var verification = verificationScope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
        (await verification.EntryShares.AnyAsync(x => x.Id == share.Id, TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await verification.EntryShareSessions.AnyAsync(x => x.ShareId == share.Id, TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await verification.EntryShareCreationChallenges.AnyAsync(x => x.ShareId == reservation.ShareId, TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await verification.EntryShareActivities.SingleAsync(x => x.ShareId == share.Id, TestContext.Current.CancellationToken))
            .PublishedAt.ShouldBeNull();
        await CleanupAsync();
        (await JournalIdsAsync(source)).ShouldBe([share.Id]);
    }

    private async Task CleanupAsync(IClock? clock = null)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>();
        await new ExpireEntrySharesJob(writer,
            Options.Create(new ExpireEntrySharesJobOptions { BatchSize = 1, MaximumBatches = 1 }),
            clock ?? apiFactory.FakeClock).ExecuteAsync(TestContext.Current.CancellationToken);
    }

    private async Task<Guid[]> JournalIdsAsync(EntryScope source)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<VaultDbReadContext>().Set<EntryShareActivity>()
            .Where(x => x.OrganizationId == source.OrganizationId).Select(x => x.ShareId)
            .ToArrayAsync(TestContext.Current.CancellationToken);
    }

    private static EntryShare Share(EntryScope source, Guid sender, Instant now) => EntryShare.Create(
        Guid.NewGuid(), source, new EntryRevision(1), sender, now, now + Duration.FromHours(1), 2,
        EntryShareRecipientMode.AnyoneWithLink, null, EntryShareProtection.None, null,
        new byte[32], new byte[24], new byte[16], false, 1, now);
}
