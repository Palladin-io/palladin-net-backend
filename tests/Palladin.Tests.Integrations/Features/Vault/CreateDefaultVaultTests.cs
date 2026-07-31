using System.Net;
using System.Net.Http.Json;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Palladin.Core.Security;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault;

[Collection<ApiFactoryCollection>]
public sealed class CreateDefaultVaultTests(ApiFactory apiFactory) : TestBase
{
    private sealed record ChallengeIdResponse(Guid VaultId, System.Text.Json.JsonElement ExpiresAt);

    [Fact]
    public async Task When_ValidCiphertextIsSubmitted_Then_ServerAssignsDefaultFlag()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        await apiFactory.Services.SeedMemberKeyDirectoryAsync(user.Id, VaultContractFaker.MemberPublicKey);
        var client = apiFactory.CreateAuthenticatedClient(user);
        var challengeResponse = await client.PostAsync("api/vaults/creation-challenges", null);
        var challenge = await challengeResponse.Content.ReadFromJsonAsync<ChallengeIdResponse>();
        var request = VaultContractFaker.CreateRequest(organization.Id, challenge!.VaultId, user.Id);

        var (response, result) = await client
            .POSTAsync<CreateDefaultVaultEndpoint, CreateVaultRequest, CreateVaultResponse>(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        result!.IsDefault.ShouldBeTrue();
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await readContext.Vaults.SingleAsync(v => v.Id == result.Id)).IsDefault.ShouldBeTrue();
        (await readContext.VaultMemberKeyEnvelopes.SingleAsync(x => x.VaultId == result.Id && x.MemberId == user.Id))
            .SealedVaultKeyPackage.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task When_UserAlreadyHasDefaultVault_Then_Returns409()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id, isDefault: true);
        var client = apiFactory.CreateAuthenticatedClient(user);
        var challengeResponse = await client.PostAsync("api/vaults/creation-challenges", null);
        var challenge = await challengeResponse.Content.ReadFromJsonAsync<ChallengeIdResponse>();

        var response = await client.PostAsJsonAsync(
            "api/account/default-vault",
            VaultContractFaker.CreateRequest(organization.Id, challenge!.VaultId, user.Id));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task When_TwoChallengesRace_Then_OneCreatedAndOneConflicts()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        await apiFactory.Services.SeedMemberKeyDirectoryAsync(user.Id, VaultContractFaker.MemberPublicKey);
        var client = apiFactory.CreateAuthenticatedClient(user);
        async Task<CreateVaultRequest> RequestAsync()
        {
            var issued = await client.PostAsync("api/vaults/creation-challenges", null);
            var challenge = await issued.Content.ReadFromJsonAsync<ChallengeIdResponse>();
            return VaultContractFaker.CreateRequest(organization.Id, challenge!.VaultId, user.Id);
        }

        var first = client.PostAsJsonAsync("api/account/default-vault", await RequestAsync());
        var second = client.PostAsJsonAsync("api/account/default-vault", await RequestAsync());
        var statuses = new[] { (await first).StatusCode, (await second).StatusCode };

        statuses.ShouldContain(HttpStatusCode.Created);
        statuses.ShouldContain(HttpStatusCode.Conflict);
        statuses.ShouldNotContain(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task When_UserMissingVaultCreate_Then_Returns403()
    {
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var response = await apiFactory.CreateAuthenticatedClient(user, Permission.AuditView)
            .PostAsync("api/vaults/creation-challenges", null);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
