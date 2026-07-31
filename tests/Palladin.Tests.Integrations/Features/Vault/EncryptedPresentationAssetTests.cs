using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using FastEndpoints;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Palladin.Core.Types;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Infrastructure.Assets;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Purge;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault;

[Collection<ApiFactoryCollection>]
public sealed class EncryptedPresentationAssetTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_MemberUploadsEncryptedEntryAsset_Then_OnlyOpaquePrivateCiphertextIsStored()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var entry = await apiFactory.Services.SeedEntryAsync(vault.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);
        var ciphertext = RandomNumberGenerator.GetBytes(257);
        var request = CreateRequest(vault.Id, entry.Id, ciphertext);

        var (response, uploaded) = await client.POSTAsync<
            UploadEncryptedPresentationAssetEndpoint,
            UploadEncryptedPresentationAssetRequest,
            EncryptedPresentationAssetResponse>(request);
        var (downloadResponse, download) = await client.GETAsync<
            GetEncryptedPresentationAssetEndpoint,
            GetEncryptedPresentationAssetRequest,
            GetEncryptedPresentationAssetResponse>(new GetEncryptedPresentationAssetRequest
            {
                VaultId = vault.Id,
                AssetId = request.AssetId,
            });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        uploaded.ShouldNotBeNull();
        downloadResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        download.ShouldNotBeNull();
        download.DownloadUrl.ShouldStartWith("https://private.test/download/");
        download.DownloadUrl.ShouldContain("signature=test");
        download.CiphertextSha256.ShouldBe(request.CiphertextSha256);

        var stored = apiFactory.CdnService.Objects.Single(x => x.Value.AsSpan().SequenceEqual(ciphertext));
        stored.Key.ShouldStartWith("encrypted-assets/");
        stored.Key.ShouldNotContain(vault.Id.ToString(), Case.Insensitive);
        stored.Key.ShouldNotContain(entry.Id.ToString(), Case.Insensitive);
        stored.Key.ShouldNotContain("example", Case.Insensitive);

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var persisted = await readContext.EncryptedPresentationAssets.AsNoTracking()
            .SingleAsync(x => x.OrganizationId == organization.Id
                              && x.VaultId == vault.Id
                              && x.Id == request.AssetId);
        persisted.EntryId.ShouldBe(entry.Id);
        persisted.CiphertextLength.ShouldBe(ciphertext.Length);
        persisted.CiphertextSha256.ShouldBe(SHA256.HashData(ciphertext));
        persisted.StorageKey.ShouldBe(stored.Key);
    }

    [Fact]
    public async Task When_AssetScopeOrDigestIsInvalid_Then_UploadFailsClosed()
    {
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, owner.Id);
        var entry = await apiFactory.Services.SeedEntryAsync(vault.Id, owner.Id);
        var outsider = await apiFactory.Services.SeedAdditionalOrganizationMemberAsync(organization.Id);
        var outsiderClient = apiFactory.CreateAuthenticatedClient(outsider);
        var ownerClient = apiFactory.CreateAuthenticatedClient(owner);
        var ciphertext = RandomNumberGenerator.GetBytes(64);
        var request = CreateRequest(vault.Id, entry.Id, ciphertext);

        var forbidden = await outsiderClient.PostAsJsonAsync($"api/vaults/{vault.Id}/assets", request);
        var invalidDigest = await ownerClient.PostAsJsonAsync(
            $"api/vaults/{vault.Id}/assets",
            request with { AssetId = Guid.NewGuid(), CiphertextSha256 = WebEncoders.Base64UrlEncode(new byte[32]) });
        var invalidMedia = await ownerClient.PostAsJsonAsync(
            $"api/vaults/{vault.Id}/assets",
            request with { AssetId = Guid.NewGuid(), MediaType = "image/svg+xml" });

        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        invalidDigest.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        invalidMedia.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task When_EntryBelongsToAnotherVault_Then_TenantFirstUploadReturns404()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var owningVault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var otherVault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var entry = await apiFactory.Services.SeedEntryAsync(otherVault.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);
        var request = CreateRequest(owningVault.Id, entry.Id, RandomNumberGenerator.GetBytes(64));

        var response = await client.PostAsJsonAsync($"api/vaults/{owningVault.Id}/assets", request);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task When_AssetIdIsRetried_Then_ExactCiphertextIsIdempotentAndSubstitutionIsRejected()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);
        var entryId = await CreateEntryAsync(client, organization.Id, vault.Id);
        var request = CreateRequest(vault.Id, entryId, RandomNumberGenerator.GetBytes(64));

        var first = await client.PostAsJsonAsync($"api/vaults/{vault.Id}/assets", request);
        var retry = await client.PostAsJsonAsync($"api/vaults/{vault.Id}/assets", request);
        var replacementBytes = RandomNumberGenerator.GetBytes(64);
        var substitution = await client.PostAsJsonAsync(
            $"api/vaults/{vault.Id}/assets",
            request with
            {
                Ciphertext = replacementBytes,
                CiphertextSha256 = WebEncoders.Base64UrlEncode(SHA256.HashData(replacementBytes)),
            });

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        substitution.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await readContext.EncryptedPresentationAssets.CountAsync(x => x.Id == request.AssetId)).ShouldBe(1);
    }

    [Fact]
    public async Task When_ExactUploadRaces_Then_OneAssetAndOneObjectRemain()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);
        var entryId = await CreateEntryAsync(client, organization.Id, vault.Id);
        var ciphertext = RandomNumberGenerator.GetBytes(64);
        var request = CreateRequest(vault.Id, entryId, ciphertext);

        var responses = await Task.WhenAll(
            client.PostAsJsonAsync($"api/vaults/{vault.Id}/assets", request),
            client.PostAsJsonAsync($"api/vaults/{vault.Id}/assets", request));

        responses.ShouldAllBe(x => x.IsSuccessStatusCode);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await readContext.EncryptedPresentationAssets.CountAsync(x => x.Id == request.AssetId)).ShouldBe(1);
        apiFactory.CdnService.Objects.Values.Count(x => x.AsSpan().SequenceEqual(ciphertext)).ShouldBe(1);
    }

    [Fact]
    public async Task When_AssetIsDeletedDuringUpload_Then_UploadedObjectIsNotOrphaned()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var entry = await apiFactory.Services.SeedEntryAsync(vault.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);
        var ciphertext = RandomNumberGenerator.GetBytes(64);
        var request = CreateRequest(vault.Id, entry.Id, ciphertext);
        apiFactory.CdnService.AfterCopyAsync = async (_, cancellationToken) =>
        {
            await using var scope = apiFactory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>()
                .EncryptedPresentationAssets
                .Where(x => x.OrganizationId == organization.Id
                            && x.VaultId == vault.Id
                            && x.Id == request.AssetId)
                .ExecuteDeleteAsync(cancellationToken);
        };

        HttpResponseMessage response;
        try
        {
            response = await client.PostAsJsonAsync(
                $"api/vaults/{vault.Id}/assets",
                request,
                TestContext.Current.CancellationToken);
        }
        finally
        {
            apiFactory.CdnService.AfterCopyAsync = null;
        }

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        apiFactory.CdnService.Objects.Values.Any(x => x.AsSpan().SequenceEqual(ciphertext)).ShouldBeFalse();
    }

    [Fact]
    public async Task When_EntryIsDeletedAndRestored_Then_EncryptedAssetRemainsAvailable()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);
        var entryId = await CreateEntryAsync(client, organization.Id, vault.Id);
        var request = CreateRequest(vault.Id, entryId, RandomNumberGenerator.GetBytes(64));
        (await client.PostAsJsonAsync($"api/vaults/{vault.Id}/assets", request)).EnsureSuccessStatusCode();

        var delete = EntryEnvelopeFaker.CreateStateChangeRequest(
            organization.Id,
            vault.Id,
            entryId,
            1,
            EntryOperation.Deleted);
        var (deleteResponse, _) = await client.POSTAsync<
            DeleteEntryEndpoint,
            ChangeEntryStateRequest,
            ChangeEntryStateResponse>(delete);
        var whileDeleted = await client.GetAsync($"api/vaults/{vault.Id}/assets/{request.AssetId}");

        var restore = EntryEnvelopeFaker.CreateStateChangeRequest(
            organization.Id,
            vault.Id,
            entryId,
            2,
            EntryOperation.Restored,
            agentDiscoveryRevision: 2,
            seed: 64);
        var (restoreResponse, _) = await client.POSTAsync<
            RestoreEntryEndpoint,
            ChangeEntryStateRequest,
            ChangeEntryStateResponse>(restore);
        var afterRestore = await client.GetAsync($"api/vaults/{vault.Id}/assets/{request.AssetId}");

        deleteResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        whileDeleted.StatusCode.ShouldBe(HttpStatusCode.OK);
        restoreResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        afterRestore.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task When_EntryAssetsArePurged_Then_MetadataAndEveryObjectVersionAreRemoved()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var entry = await apiFactory.Services.SeedEntryAsync(vault.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);
        var ciphertext = RandomNumberGenerator.GetBytes(64);
        var request = CreateRequest(vault.Id, entry.Id, ciphertext);
        (await client.PostAsJsonAsync($"api/vaults/{vault.Id}/assets", request)).EnsureSuccessStatusCode();

        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var domainContext = scope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>();
            var purger = new CdnEntryAssetPurger(domainContext, apiFactory.CdnService);
            await purger.PurgeAsync(
                new EntryScope(organization.Id, vault.Id, entry.Id),
                TestContext.Current.CancellationToken);
            await domainContext.CommitAsync(TestContext.Current.CancellationToken);
        }

        apiFactory.CdnService.Objects.Values.Any(x => x.AsSpan().SequenceEqual(ciphertext)).ShouldBeFalse();
        await using var verificationScope = apiFactory.Services.CreateAsyncScope();
        var readContext = verificationScope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await readContext.EncryptedPresentationAssets.AnyAsync(x => x.Id == request.AssetId)).ShouldBeFalse();
    }

    [Fact]
    public async Task When_AssetIsExplicitlyDeleted_Then_ObjectAndMetadataAreRemovedIdempotently()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var entry = await apiFactory.Services.SeedEntryAsync(vault.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);
        var ciphertext = RandomNumberGenerator.GetBytes(64);
        var request = CreateRequest(vault.Id, entry.Id, ciphertext);
        (await client.PostAsJsonAsync($"api/vaults/{vault.Id}/assets", request)).EnsureSuccessStatusCode();

        var first = await client.DeleteAsync($"api/vaults/{vault.Id}/assets/{request.AssetId}");
        var retry = await client.DeleteAsync($"api/vaults/{vault.Id}/assets/{request.AssetId}");

        first.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        retry.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        apiFactory.CdnService.Objects.Values.Any(x => x.AsSpan().SequenceEqual(ciphertext)).ShouldBeFalse();
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await readContext.EncryptedPresentationAssets.AnyAsync(x => x.Id == request.AssetId)).ShouldBeFalse();
    }

    [Fact]
    public async Task When_ZeroKnowledgeCutoverRuns_Then_AllLegacyPrefixesArePurgedAndOpaqueAssetsRemain()
    {
        await StoreAsync("favicons/example.com", [1]);
        await StoreAsync("entry-icons/plain-vault/plain-entry.png", [2]);
        await StoreAsync("vault-icons/plain-vault/icon.png", [3]);
        await StoreAsync("encrypted-assets/019bf50ab12e70008000000000000000", [4]);
        var purger = new LegacyPresentationAssetPurger(apiFactory.CdnService);

        await purger.PurgeAndVerifyAsync(TestContext.Current.CancellationToken);

        apiFactory.CdnService.Objects.Keys.ShouldNotContain(x => x.StartsWith("favicons/", StringComparison.Ordinal));
        apiFactory.CdnService.Objects.Keys.ShouldNotContain(x => x.StartsWith("entry-icons/", StringComparison.Ordinal));
        apiFactory.CdnService.Objects.Keys.ShouldNotContain(x => x.StartsWith("vault-icons/", StringComparison.Ordinal));
        apiFactory.CdnService.Objects.ShouldContainKey("encrypted-assets/019bf50ab12e70008000000000000000");
    }

    private static UploadEncryptedPresentationAssetRequest CreateRequest(
        Guid vaultId,
        Guid entryId,
        byte[] ciphertext) => new()
        {
            VaultId = vaultId,
            AssetId = Guid.NewGuid(),
            Target = PresentationAssetTargetContract.Entry,
            EntryId = entryId,
            MediaType = "image/png",
            Ciphertext = ciphertext,
            CiphertextSha256 = WebEncoders.Base64UrlEncode(SHA256.HashData(ciphertext)),
        };

    private async Task<Guid> CreateEntryAsync(HttpClient client, Guid organizationId, Guid vaultId)
    {
        var (_, challenge) = await client.POSTAsync<
            IssueEntryCreationChallengeEndpoint,
            IssueEntryCreationChallengeRequest,
            IssueEntryCreationChallengeResponse>(new IssueEntryCreationChallengeRequest { VaultId = vaultId });
        var entryId = challenge!.Items.Single().EntryId;
        var (response, _) = await client.POSTAsync<
            CreateEntryEndpoint,
            CreateEntryRequest,
            CreateEntryResponse>(EntryEnvelopeFaker.CreateRequest(organizationId, vaultId, entryId));
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return entryId;
    }

    private async Task StoreAsync(string key, byte[] value)
    {
        await using var stream = new MemoryStream(value, writable: false);
        await apiFactory.CdnService.PutAsync(
            stream,
            key,
            TestContext.Current.CancellationToken);
    }

}
