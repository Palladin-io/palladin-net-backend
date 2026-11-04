using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Palladin.Core.Cryptography;
using Palladin.Core.Json;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Shared;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault;

[Collection<ApiFactoryCollection>]
public sealed class MemberKeyMigrationTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_UpgradedMemberRewrapsLegacyEnvelope_Then_ReportReachesZeroAndRetryIsIdempotent()
    {
        // Given
        var scenario = await SeedUpgradedMemberWithLegacyEnvelopeAsync();
        var before = await ReportAsync(scenario.Client);
        var request = RewrapRequest(scenario);

        // When
        var rewrap = await scenario.Client.PutAsJsonAsync(
            $"api/vaults/{scenario.VaultId}/members/me/key-envelope", request, PalladinJsonSerializationSettings.DefaultOptions);
        var retry = await scenario.Client.PutAsJsonAsync(
            $"api/vaults/{scenario.VaultId}/members/me/key-envelope", request, PalladinJsonSerializationSettings.DefaultOptions);
        var after = await ReportAsync(scenario.Client);

        // Then
        before.MemberEnvelopes.ShouldBe(1);
        before.MemberEnvelopesPendingRewrap.ShouldBe(1);
        before.MemberKey!.KeyVersion.ShouldBe(2u);
        before.MemberKey.SuiteId.ShouldBe(KeyWrapperSuites.Current.Id);
        rewrap.StatusCode.ShouldBe(HttpStatusCode.OK);
        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        after.MemberEnvelopesPendingRewrap.ShouldBe(0);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<VaultDbReadContext>().VaultMemberKeyEnvelopes
            .SingleAsync(x => x.VaultId == scenario.VaultId && x.MemberId == scenario.UserId);
        stored.WrapperSuiteId.ShouldBe(KeyWrapperSuites.Current.Id);
        stored.RecipientKeyVersion.Value.ShouldBe(2u);
        stored.SealedVaultKeyPackage.Length.ShouldBe(KeyWrapperSuites.Current.PackageBytes);
    }

    [Fact]
    public async Task When_ExpectedRecipientVersionIsStale_Then_ConflictKeepsStoredEnvelope()
    {
        // Given
        var scenario = await SeedUpgradedMemberWithLegacyEnvelopeAsync();

        // When
        var response = await scenario.Client.PutAsJsonAsync(
            $"api/vaults/{scenario.VaultId}/members/me/key-envelope",
            RewrapRequest(scenario) with { ExpectedRecipientKeyVersion = 2 },
            PalladinJsonSerializationSettings.DefaultOptions);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReportAsync(scenario.Client)).MemberEnvelopesPendingRewrap.ShouldBe(1);
    }

    [Fact]
    public async Task When_RewrapUsesReadOnlySuite_Then_RejectedWithoutChange()
    {
        // Given
        var scenario = await SeedUpgradedMemberWithLegacyEnvelopeAsync();
        var request = RewrapRequest(scenario);
        var legacySuite = KeyWrapperSuites.Resolve(LegacyWrapperSuite.Id);
        var wrapped = request.WrappedVaultKey.WrappedVaultKey;

        // When
        var response = await scenario.Client.PutAsJsonAsync(
            $"api/vaults/{scenario.VaultId}/members/me/key-envelope",
            request with
            {
                WrappedVaultKey = new MemberVaultKeyEnvelopeContract(new WrappedKeyContract(
                    wrapped.Descriptor with { WrapperSuiteId = legacySuite.Id },
                    WebEncoders.Base64UrlEncode(new byte[legacySuite.PackageBytes]))),
            },
            PalladinJsonSerializationSettings.DefaultOptions);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReportAsync(scenario.Client)).MemberEnvelopesPendingRewrap.ShouldBe(1);
    }

    [Fact]
    public async Task When_CallerIsNotAVaultMember_Then_NotFound()
    {
        // Given
        var scenario = await SeedUpgradedMemberWithLegacyEnvelopeAsync();
        var (outsider, _, _) = await apiFactory.Services.SeedUserAsync();

        // When
        var response = await apiFactory.CreateAuthenticatedClient(outsider).PutAsJsonAsync(
            $"api/vaults/{scenario.VaultId}/members/me/key-envelope", RewrapRequest(scenario),
            PalladinJsonSerializationSettings.DefaultOptions);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private async Task<Scenario> SeedUpgradedMemberWithLegacyEnvelopeAsync()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id, VaultFaker.Create(
            organizationId: organization.Id, createdBy: user.Id, memberWrapperSuiteId: LegacyWrapperSuite.Id));
        var publicKey = RandomNumberGenerator.GetBytes(KeyWrapperSuites.Current.PublicKeyBytes);
        await apiFactory.Services.SeedMemberKeyDirectoryAsync(user.Id, publicKey, 2);
        return new Scenario(organization.Id, vault.Id, user.Id, publicKey, apiFactory.CreateAuthenticatedClient(user));
    }

    private static RewrapMemberVaultKeyRequest RewrapRequest(Scenario scenario) => new()
    {
        ExpectedRecipientKeyVersion = 1,
        WrappedVaultKey = new MemberVaultKeyEnvelopeContract(new WrappedKeyContract(
            new KeyWrapperDescriptorContract(2, KeyWrapperSuites.Current.Id, KeyWrapperPurposeContract.MemberVaultKey,
                new EnvelopeScopeContract(scenario.OrganizationId, scenario.VaultId, MemberId: scenario.UserId),
                "1", 1, 1, RecipientKeyKindContract.MemberEncryption, 2,
                WebEncoders.Base64UrlEncode(RecipientKeyFingerprint.Compute(
                    KeyWrapperSuites.Current.Id, VaultKeyKind.MemberEncryption, scenario.PublicKey)),
                null),
            WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(KeyWrapperSuites.Current.PackageBytes)))),
    };

    private static async Task<KeyMigrationReportResponse> ReportAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<KeyMigrationReportResponse>(
            "api/account/key-migration", PalladinJsonSerializationSettings.DefaultOptions))!;

    private sealed record Scenario(Guid OrganizationId, Guid VaultId, Guid UserId, byte[] PublicKey, HttpClient Client);
}
