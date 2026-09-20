using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Palladin.Core.Types;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Sharing;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault;

[Collection<ApiFactoryCollection>]
public sealed class EntrySharingConcurrencyTests(ApiFactory apiFactory) : TestBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task When_AuthorityIsRevokedAfterReceiptAuthorization_Then_TheReceiptCannotCommit(bool entireOrganization)
    {
        // Given
        var seeded = await SeedShareAsync();
        await using var receiptScope = apiFactory.Services.CreateAsyncScope();
        await using var revokeScope = apiFactory.Services.CreateAsyncScope();
        var receipt = receiptScope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>();
        var revoke = revokeScope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>();
        var share = await receipt.EntryShares.SingleAsync(x => x.Id == seeded.ShareId, TestContext.Current.CancellationToken);
        var session = await receipt.EntryShareSessions.SingleAsync(x => x.ShareId == share.Id, TestContext.Current.CancellationToken);
        await receiptScope.ServiceProvider.GetRequiredService<EntryShareAuthority>()
            .EnsureRecipientSourceAsync(share, TestContext.Current.CancellationToken);

        // When
        if (entireOrganization)
        {
            var organization = await revoke.VaultOrganizationLifecycles.SingleAsync(
                x => x.OrganizationId == share.OrganizationId, TestContext.Current.CancellationToken);
            organization.DisableSharing();
        }
        else
        {
            var sender = await revoke.EntryShareSenderAuthorities.SingleAsync(
                x => x.OrganizationId == share.OrganizationId && x.UserId == share.CreatedBy, TestContext.Current.CancellationToken);
            sender.RevokeThrough(share.SenderAuthorizationVersion);
        }

        await revoke.CommitAsync(TestContext.Current.CancellationToken);
        share.Deliver(session, seeded.Now);

        // Then
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => receipt.CommitAsync(TestContext.Current.CancellationToken));
        await AssertNoReceiptAsync(share.Id);
        await using var freshScope = apiFactory.Services.CreateAsyncScope();
        await Should.ThrowAsync<EntryShareUnavailableException>(() => freshScope.ServiceProvider
            .GetRequiredService<EntryShareAuthority>().EnsureRecipientSourceAsync(share, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task When_PurgeStartsWithAnUnchangedTimestamp_Then_TheStaleReceiptStillCannotCommit()
    {
        // Given
        var seeded = await SeedShareAsync();
        await using var receiptScope = apiFactory.Services.CreateAsyncScope();
        await using var purgeScope = apiFactory.Services.CreateAsyncScope();
        var receipt = receiptScope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>();
        var purge = purgeScope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>();
        var share = await receipt.EntryShares.SingleAsync(x => x.Id == seeded.ShareId, TestContext.Current.CancellationToken);
        var session = await receipt.EntryShareSessions.SingleAsync(x => x.ShareId == share.Id, TestContext.Current.CancellationToken);
        await receiptScope.ServiceProvider.GetRequiredService<EntryShareAuthority>()
            .EnsureRecipientSourceAsync(share, TestContext.Current.CancellationToken);
        var entry = await purge.Entries.SingleAsync(x => x.OrganizationId == share.OrganizationId
            && x.VaultId == share.VaultId && x.Id == share.EntryId, TestContext.Current.CancellationToken);

        // When
        entry.BeginPurge(share.CreatedBy, true, entry.UpdatedAt);
        await purge.CommitAsync(TestContext.Current.CancellationToken);
        share.Deliver(session, seeded.Now);

        // Then
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => receipt.CommitAsync(TestContext.Current.CancellationToken));
        await AssertNoReceiptAsync(share.Id);
    }

    [Fact]
    public async Task When_TwoSessionsRaceForTheLastReceipt_Then_OnlyOneDeliveryAndActivityCommit()
    {
        // Given
        var seeded = await SeedShareAsync(sessionCount: 2);
        await using var firstScope = apiFactory.Services.CreateAsyncScope();
        await using var secondScope = apiFactory.Services.CreateAsyncScope();
        var first = firstScope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>();
        var second = secondScope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>();
        var firstShare = await first.EntryShares.SingleAsync(x => x.Id == seeded.ShareId, TestContext.Current.CancellationToken);
        var secondShare = await second.EntryShares.SingleAsync(x => x.Id == seeded.ShareId, TestContext.Current.CancellationToken);
        var sessions = await first.EntryShareSessions.Where(x => x.ShareId == seeded.ShareId).OrderBy(x => x.Id).ToListAsync(TestContext.Current.CancellationToken);
        var otherSession = await second.EntryShareSessions.SingleAsync(x => x.ShareId == seeded.ShareId && x.Id == sessions[1].Id, TestContext.Current.CancellationToken);
        await firstScope.ServiceProvider.GetRequiredService<EntryShareAuthority>()
            .EnsureRecipientSourceAsync(firstShare, TestContext.Current.CancellationToken);
        await secondScope.ServiceProvider.GetRequiredService<EntryShareAuthority>()
            .EnsureRecipientSourceAsync(secondShare, TestContext.Current.CancellationToken);

        // When
        firstShare.Deliver(sessions[0], seeded.Now);
        secondShare.Deliver(otherSession, seeded.Now);
        await first.CommitAsync(TestContext.Current.CancellationToken);

        // Then
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => second.CommitAsync(TestContext.Current.CancellationToken));
        await using var verificationScope = apiFactory.Services.CreateAsyncScope();
        var verification = verificationScope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
        (await verification.EntryShares.SingleAsync(x => x.Id == seeded.ShareId, TestContext.Current.CancellationToken)).DeliveryCount.ShouldBe(1);
        (await verification.EntryShareSessions.CountAsync(x => x.ShareId == seeded.ShareId && x.DeliveredAt != null, TestContext.Current.CancellationToken)).ShouldBe(1);
        (await verification.EntryShareActivities.CountAsync(x => x.ShareId == seeded.ShareId
            && x.Kind == EntryShareActivityKind.Delivered, TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task When_TheSameReceiptIsRetriedInAFreshContext_Then_ItDoesNotConsumeOrPublishTwice()
    {
        // Given
        var seeded = await SeedShareAsync();

        // When
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var scope = apiFactory.Services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>();
            var share = await context.EntryShares.SingleAsync(x => x.Id == seeded.ShareId, TestContext.Current.CancellationToken);
            var session = await context.EntryShareSessions.SingleAsync(x => x.ShareId == share.Id, TestContext.Current.CancellationToken);
            await scope.ServiceProvider.GetRequiredService<EntryShareAuthority>()
                .EnsureRecipientSourceAsync(share, TestContext.Current.CancellationToken);
            share.Deliver(session, seeded.Now).ShouldBe(attempt == 0);
            await context.CommitAsync(TestContext.Current.CancellationToken);
        }

        // Then
        await using var verificationScope = apiFactory.Services.CreateAsyncScope();
        var verification = verificationScope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
        (await verification.EntryShares.SingleAsync(x => x.Id == seeded.ShareId, TestContext.Current.CancellationToken)).DeliveryCount.ShouldBe(1);
        (await verification.EntryShareActivities.CountAsync(x => x.ShareId == seeded.ShareId
            && x.Kind == EntryShareActivityKind.Delivered, TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    private async Task<(Guid ShareId, Instant Now)> SeedShareAsync(int sessionCount = 1)
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var entryId = await apiFactory.Services.SeedSyncEntryAsync(organization.Id, vault.Id, user.Id);
        var now = Instant.FromUnixTimeMilliseconds(apiFactory.FakeClock.GetCurrentInstant().ToUnixTimeMilliseconds());
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>();
        var source = await scope.ServiceProvider.GetRequiredService<EntryShareAuthority>()
            .LoadSenderSourceAsync(new EntryScope(organization.Id, vault.Id, entryId), user.Id, 1, TestContext.Current.CancellationToken);
        var share = EntryShare.Create(Guid.NewGuid(), new EntryScope(organization.Id, vault.Id, entryId),
            source.Entry.CurrentRevision, user.Id, now, now + Duration.FromHours(1), 1,
            EntryShareRecipientMode.AnyoneWithLink, null, EntryShareProtection.None, null,
            new byte[32], new byte[24], new byte[16], true, 1, source.Member.AddedAt);
        context.Add(share);
        for (var index = 0; index < sessionCount; index++)
        {
            context.Add(share.OpenSession(Guid.NewGuid(), new byte[32], now, Duration.FromMinutes(15)));
        }

        await context.CommitAsync(TestContext.Current.CancellationToken);
        return (share.Id, now);
    }

    private async Task AssertNoReceiptAsync(Guid shareId)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
        (await context.EntryShares.SingleAsync(x => x.Id == shareId, TestContext.Current.CancellationToken)).DeliveryCount.ShouldBe(0);
        (await context.EntryShareSessions.Where(x => x.ShareId == shareId).ToListAsync(TestContext.Current.CancellationToken))
            .ShouldAllBe(x => x.DeliveredAt == null);
        (await context.EntryShareActivities.AnyAsync(x => x.ShareId == shareId
            && x.Kind == EntryShareActivityKind.Delivered, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }
}
