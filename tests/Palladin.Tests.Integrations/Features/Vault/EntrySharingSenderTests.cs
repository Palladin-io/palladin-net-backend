using System.Net;
using System.Security.Cryptography;
using FastEndpoints;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Palladin.Core.Types;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Sharing;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Mocks;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault;

[Collection<ApiFactoryCollection>]
public sealed class EntrySharingSenderTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_CreationIsRetried_Then_OneSnapshotIsStoredAndTheListContainsNoDeliveryMaterial()
    {
        // Given
        var (client, request, _) = await SeedRequestAsync();

        // When
        var first = await client.POSTAsync<CreateEntryShareEndpoint, CreateEntryShareRequest, CreateEntryShareResponse>(request);
        var retry = await client.POSTAsync<CreateEntryShareEndpoint, CreateEntryShareRequest, CreateEntryShareResponse>(request);
        var listed = await client.GETAsync<ListEntrySharesEndpoint, ListEntrySharesRequest, ListEntrySharesResponse>(
            new ListEntrySharesRequest { VaultId = request.VaultId, EntryId = request.EntryId });

        // Then
        first.Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        retry.Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        retry.Result.ShareId.ShouldBe(first.Result.ShareId);
        listed.Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        listed.Response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var item = listed.Result.Items.ShouldHaveSingleItem();
        item.Status.ShouldBe("active");
        item.RecipientEmail.ShouldBe("recipient@example.test");
        item.NotifyOnFirstReceipt.ShouldBeTrue();
        item.Protection.ShouldBe(EntryShareProtection.None);
        var json = await listed.Response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        json.ShouldNotContain("ciphertext", Case.Insensitive);
        json.ShouldNotContain("nonce", Case.Insensitive);
        json.ShouldNotContain("accessToken", Case.Insensitive);
        json.ShouldNotContain("verifier", Case.Insensitive);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
        (await db.EntryShares.CountAsync(x => x.Id == request.ShareId, TestContext.Current.CancellationToken)).ShouldBe(1);
        (await db.EntryShareActivities.CountAsync(x => x.ShareId == request.ShareId, TestContext.Current.CancellationToken)).ShouldBe(1);
        (await db.EntryShareCreationChallenges.AnyAsync(x => x.ShareId == request.ShareId, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task When_ADeletedEntryIsRestored_Then_ItsOldSharingSnapshotStaysRevoked()
    {
        // Given
        var (client, request, organizationId) = await SeedRequestAsync();
        var created = await client.POSTAsync<CreateEntryShareEndpoint, CreateEntryShareRequest>(request);
        created.StatusCode.ShouldBe(HttpStatusCode.OK);

        // When
        var deleted = await client.POSTAsync<DeleteEntryEndpoint, ChangeEntryStateRequest>(
            EntryEnvelopeFaker.CreateStateChangeRequest(organizationId, request.VaultId, request.EntryId, 1, EntryOperation.Deleted));
        var restored = await client.POSTAsync<RestoreEntryEndpoint, ChangeEntryStateRequest>(
            EntryEnvelopeFaker.CreateStateChangeRequest(organizationId, request.VaultId, request.EntryId, 2, EntryOperation.Restored));

        // Then
        deleted.StatusCode.ShouldBe(HttpStatusCode.OK);
        restored.StatusCode.ShouldBe(HttpStatusCode.OK);
        var listed = await client.GETAsync<ListEntrySharesEndpoint, ListEntrySharesRequest, ListEntrySharesResponse>(
            new ListEntrySharesRequest { VaultId = request.VaultId, EntryId = request.EntryId });
        listed.Result.Items.ShouldHaveSingleItem().Status.ShouldBe("revoked");
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var share = await scope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>().EntryShares
            .SingleAsync(x => x.Id == request.ShareId, TestContext.Current.CancellationToken);
        await Should.ThrowAsync<EntryShareUnavailableException>(() => scope.ServiceProvider
            .GetRequiredService<EntryShareAuthority>().EnsureRecipientSourceAsync(share, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task When_TheSenderRevokesTwice_Then_DeliveryMaterialIsErasedAndOneRevocationIsRecorded()
    {
        // Given
        var (client, request, _) = await SeedRequestAsync();
        var created = await client.POSTAsync<CreateEntryShareEndpoint, CreateEntryShareRequest>(request);
        created.StatusCode.ShouldBe(HttpStatusCode.OK);

        // When
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var revoked = await client.DELETEAsync<RevokeEntryShareEndpoint, RevokeEntryShareRequest>(
                new RevokeEntryShareRequest(request.VaultId, request.EntryId, request.ShareId));
            revoked.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
        var share = await db.EntryShares.SingleAsync(x => x.Id == request.ShareId, TestContext.Current.CancellationToken);
        share.Ciphertext.ShouldBeEmpty();
        share.Nonce.ShouldBeEmpty();
        share.AccessTokenHash.ShouldBeEmpty();
        share.ProtectedRecipientEmail.ShouldBeNull();
        (await db.EntryShareActivities.CountAsync(x => x.ShareId == request.ShareId
            && x.Kind == EntryShareActivityKind.RevokedBySender, TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task When_AForeignMemberUsesTheReservation_Then_NoSnapshotIsCreated()
    {
        // Given
        var (_, request, _) = await SeedRequestAsync();
        var (outsider, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(outsider);

        // When
        var response = await client.POSTAsync<CreateEntryShareEndpoint, CreateEntryShareRequest>(request);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>().EntryShares
            .AnyAsync(x => x.Id == request.ShareId, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    private async Task<(HttpClient Client, CreateEntryShareRequest Request, Guid OrganizationId)> SeedRequestAsync()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var entryId = await apiFactory.Services.SeedSyncEntryAsync(organization.Id, vault.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);
        apiFactory.MockId(Guid.NewGuid());
        var challenge = await client.POSTAsync<IssueEntryShareCreationChallengeEndpoint,
            IssueEntryShareCreationChallengeRequest, IssueEntryShareCreationChallengeResponse>(
            new IssueEntryShareCreationChallengeRequest(vault.Id, entryId));
        challenge.Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (client, new CreateEntryShareRequest
        {
            VaultId = vault.Id,
            EntryId = entryId,
            ShareId = challenge.Result.ShareId,
            SourceRevision = challenge.Result.SourceRevision,
            ExpiresAt = Instant.FromUnixTimeMilliseconds(apiFactory.FakeClock.GetCurrentInstant().ToUnixTimeMilliseconds())
                + Duration.FromHours(1),
            RecipientEmail = "recipient@example.test",
            AccessToken = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32)),
            Nonce = RandomNumberGenerator.GetBytes(24),
            Ciphertext = RandomNumberGenerator.GetBytes(64),
            NotifyOnFirstReceipt = true,
        }, organization.Id);
    }
}
