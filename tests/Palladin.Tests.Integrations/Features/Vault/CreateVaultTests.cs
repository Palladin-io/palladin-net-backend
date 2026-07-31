using System.Net;
using System.Net.Http.Json;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Palladin.Core.Security;
using Palladin.Module.Vault.Contracts.Commands;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Mocks;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault;

[Collection<ApiFactoryCollection>]
public sealed class CreateVaultTests(ApiFactory apiFactory) : TestBase
{
    private sealed record ChallengeIdResponse(Guid VaultId, System.Text.Json.JsonElement ExpiresAt);

    [Fact]
    public async Task When_ChallengeAndAuthenticatedCiphertextAreValid_Then_CreatesCanonicalVault()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        await apiFactory.Services.SeedMemberKeyDirectoryAsync(user.Id, VaultContractFaker.MemberPublicKey);
        var client = apiFactory.CreateAuthenticatedClient(user);
        var challengeResponse = await client.PostAsync("api/vaults/creation-challenges", null);
        challengeResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var challenge = await challengeResponse.Content.ReadFromJsonAsync<ChallengeIdResponse>();
        challenge.ShouldNotBeNull();
        var request = VaultContractFaker.CreateRequest(organization.Id, challenge.VaultId, user.Id);

        var (response, result) = await client
            .POSTAsync<CreateVaultEndpoint, CreateVaultRequest, CreateVaultResponse>(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        result.ShouldNotBeNull();
        result.Id.ShouldBe(challenge.VaultId);
        result.ProtocolVersion.ShouldBe((ushort)2);
        result.MemberVaultMetadata.ShouldBe(request.MemberVaultMetadata);
        result.MemberVaultKey.ShouldBe(request.CreatorVaultKey);

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var persisted = await readContext.Vaults.SingleAsync(v =>
            v.OrganizationId == organization.Id && v.Id == challenge.VaultId);
        persisted.CreatedBy.ShouldBe(user.Id);
        persisted.MemberVaultMetadataEncodedSuitePayload.ShouldNotBeEmpty();
        (await readContext.VaultMemberKeyEnvelopes.SingleAsync(x =>
            x.OrganizationId == organization.Id && x.VaultId == persisted.Id && x.MemberId == user.Id))
            .SealedVaultKeyPackage.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task When_VaultIdWasNotIssuedByServer_Then_RejectsCreation()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user);
        var request = VaultContractFaker.CreateRequest(organization.Id, Guid.NewGuid(), user.Id);

        var response = await client.PostAsJsonAsync("api/vaults", request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task When_ClientSkipsServerOwnedInitialRevision_Then_RejectsCreation()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        await apiFactory.Services.SeedMemberKeyDirectoryAsync(user.Id, VaultContractFaker.MemberPublicKey);
        var client = apiFactory.CreateAuthenticatedClient(user);
        var challengeResponse = await client.PostAsync("api/vaults/creation-challenges", null);
        var challenge = await challengeResponse.Content.ReadFromJsonAsync<ChallengeIdResponse>();
        var request = VaultContractFaker.CreateRequest(organization.Id, challenge!.VaultId, user.Id, revision: 2);

        var response = await client.PostAsJsonAsync("api/vaults", request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task When_ChallengeIsReused_Then_RejectsReplay()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        await apiFactory.Services.SeedMemberKeyDirectoryAsync(user.Id, VaultContractFaker.MemberPublicKey);
        var client = apiFactory.CreateAuthenticatedClient(user);
        var challengeResponse = await client.PostAsync("api/vaults/creation-challenges", null);
        var challenge = await challengeResponse.Content.ReadFromJsonAsync<ChallengeIdResponse>();
        var request = VaultContractFaker.CreateRequest(organization.Id, challenge!.VaultId, user.Id);

        (await client.PostAsJsonAsync("api/vaults", request)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await client.PostAsJsonAsync("api/vaults", request)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task When_CreatorEnvelopeDoesNotMatchAuthenticatedMemberKey_Then_RejectsCreation()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        await apiFactory.Services.SeedMemberKeyDirectoryAsync(
            user.Id,
            Enumerable.Repeat((byte)0xA5, 32).ToArray());
        var client = apiFactory.CreateAuthenticatedClient(user);
        var challengeResponse = await client.PostAsync("api/vaults/creation-challenges", null);
        var challenge = await challengeResponse.Content.ReadFromJsonAsync<ChallengeIdResponse>();

        var response = await client.PostAsJsonAsync(
            "api/vaults",
            VaultContractFaker.CreateRequest(organization.Id, challenge!.VaultId, user.Id));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task When_IdentityHasNoAuthenticatedMemberKey_Then_RejectsCreation()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user);
        var challengeResponse = await client.PostAsync("api/vaults/creation-challenges", null);
        var challenge = await challengeResponse.Content.ReadFromJsonAsync<ChallengeIdResponse>();

        var response = await client.PostAsJsonAsync(
            "api/vaults",
            VaultContractFaker.CreateRequest(organization.Id, challenge!.VaultId, user.Id));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task When_DirectoryCommandIsDelayed_Then_CreateFailsClosedUntilLocalProjectionIsReady()
    {
        var userFaker = UserFaker.CreateOnboarded()
            .RuleFor(x => x.PublicKey, VaultContractFaker.MemberPublicKey)
            .RuleFor(x => x.MemberKeyVersion, (uint)1);
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync(userFaker);
        var client = apiFactory.CreateAuthenticatedClient(user);
        var challengeResponse = await client.PostAsync("api/vaults/creation-challenges", null);
        var challenge = await challengeResponse.Content.ReadFromJsonAsync<ChallengeIdResponse>();

        var responseBeforeProjection = await client.PostAsJsonAsync(
            "api/vaults",
            VaultContractFaker.CreateRequest(organization.Id, challenge!.VaultId, user.Id));

        responseBeforeProjection.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        await ConsumeMemberKeyDirectoryAsync(new UpsertMemberKeyDirectoryCommand(
            user.Id,
            1,
            VaultContractFaker.MemberPublicKey,
            apiFactory.FakeClock.GetCurrentInstant()));

        var responseAfterProjection = await client.PostAsJsonAsync(
            "api/vaults",
            VaultContractFaker.CreateRequest(organization.Id, challenge.VaultId, user.Id));

        responseAfterProjection.StatusCode.ShouldBe(HttpStatusCode.Created);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var entry = await scope.ServiceProvider.GetRequiredService<VaultDbReadContext>()
            .MemberKeyDirectory.SingleAsync(x => x.UserId == user.Id);
        entry.KeyVersion.Value.ShouldBe((uint)1);
        entry.Fingerprint.ShouldBe(MemberKeyFingerprint.Compute(VaultContractFaker.MemberPublicKey));
    }

    private async Task ConsumeMemberKeyDirectoryAsync(UpsertMemberKeyDirectoryCommand command)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var consumer = new UpsertMemberKeyDirectoryConsumer(
            scope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>());
        await consumer.Consume(apiFactory.MockConsumeContext(command));
    }

    [Fact]
    public async Task When_ChallengeIsRequestedRepeatedly_Then_ReusesOneActiveRow()
    {
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user);

        var first = await (await client.PostAsync("api/vaults/creation-challenges", null))
            .Content.ReadFromJsonAsync<ChallengeIdResponse>();
        var second = await (await client.PostAsync("api/vaults/creation-challenges", null))
            .Content.ReadFromJsonAsync<ChallengeIdResponse>();

        second!.VaultId.ShouldBe(first!.VaultId);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await readContext.VaultCreationChallenges.CountAsync(x => x.RequestedBy == user.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task When_ChallengeIsRequestedConcurrently_Then_ReturnsOneServerOwnedIdentifier()
    {
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var firstClient = apiFactory.CreateAuthenticatedClient(user);
        var secondClient = apiFactory.CreateAuthenticatedClient(user);

        var responses = await Task.WhenAll(
            firstClient.PostAsync("api/vaults/creation-challenges", null),
            secondClient.PostAsync("api/vaults/creation-challenges", null));
        responses.ShouldAllBe(response => response.StatusCode == HttpStatusCode.OK);
        var challenges = await Task.WhenAll(
            responses.Select(response => response.Content.ReadFromJsonAsync<ChallengeIdResponse>()));

        challenges[0]!.VaultId.ShouldBe(challenges[1]!.VaultId);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await readContext.VaultCreationChallenges.CountAsync(x => x.RequestedBy == user.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task When_ChallengeExpires_Then_RejectsCreation()
    {
        var originalNow = apiFactory.FakeClock.GetCurrentInstant();
        try
        {
            var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
            var client = apiFactory.CreateAuthenticatedClient(user);
            var issued = await client.PostAsync("api/vaults/creation-challenges", null);
            var challenge = await issued.Content.ReadFromJsonAsync<ChallengeIdResponse>();
            apiFactory.FakeClock.Advance(Duration.FromSeconds(601));

            var response = await client.PostAsJsonAsync(
                "api/vaults",
                VaultContractFaker.CreateRequest(organization.Id, challenge!.VaultId, user.Id));

            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }
        finally
        {
            apiFactory.FakeClock.Reset(originalNow);
        }
    }

    [Fact]
    public async Task When_ChallengeExpires_Then_NextIssueReplacesItWithoutGrowingStorage()
    {
        var originalNow = apiFactory.FakeClock.GetCurrentInstant();
        try
        {
            var (user, _, _) = await apiFactory.Services.SeedUserAsync();
            var client = apiFactory.CreateAuthenticatedClient(user);
            var first = await (await client.PostAsync("api/vaults/creation-challenges", null))
                .Content.ReadFromJsonAsync<ChallengeIdResponse>();
            apiFactory.FakeClock.Advance(Duration.FromSeconds(601));

            var second = await (await client.PostAsync("api/vaults/creation-challenges", null))
                .Content.ReadFromJsonAsync<ChallengeIdResponse>();

            second!.VaultId.ShouldNotBe(first!.VaultId);
            await using var scope = apiFactory.Services.CreateAsyncScope();
            var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
            (await readContext.VaultCreationChallenges.CountAsync(x => x.RequestedBy == user.Id)).ShouldBe(1);
        }
        finally
        {
            apiFactory.FakeClock.Reset(originalNow);
        }
    }

    [Fact]
    public async Task When_ExpiredChallengeIsReplacedConcurrently_Then_ReturnsOneWinningIdentifier()
    {
        var originalNow = apiFactory.FakeClock.GetCurrentInstant();
        try
        {
            var (user, _, _) = await apiFactory.Services.SeedUserAsync();
            var initialClient = apiFactory.CreateAuthenticatedClient(user);
            var firstClient = apiFactory.CreateAuthenticatedClient(user);
            var secondClient = apiFactory.CreateAuthenticatedClient(user);
            var expired = await (await initialClient.PostAsync("api/vaults/creation-challenges", null))
                .Content.ReadFromJsonAsync<ChallengeIdResponse>();
            apiFactory.FakeClock.Advance(Duration.FromSeconds(601));

            var responses = await Task.WhenAll(
                firstClient.PostAsync("api/vaults/creation-challenges", null),
                secondClient.PostAsync("api/vaults/creation-challenges", null));

            responses.ShouldAllBe(response => response.StatusCode == HttpStatusCode.OK);
            var replacements = await Task.WhenAll(
                responses.Select(response => response.Content.ReadFromJsonAsync<ChallengeIdResponse>()));
            replacements[0]!.VaultId.ShouldBe(replacements[1]!.VaultId);
            replacements[0]!.VaultId.ShouldNotBe(expired!.VaultId);

            await using var scope = apiFactory.Services.CreateAsyncScope();
            var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
            (await readContext.VaultCreationChallenges.CountAsync(x => x.RequestedBy == user.Id)).ShouldBe(1);
        }
        finally
        {
            apiFactory.FakeClock.Reset(originalNow);
        }
    }

    [Fact]
    public async Task When_Unauthenticated_Then_Returns401()
    {
        var response = await apiFactory.CreateClient().PostAsync("api/vaults/creation-challenges", null);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task When_UserMissingVaultCreate_Then_Returns403()
    {
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.AuditView);
        var response = await client.PostAsync("api/vaults/creation-challenges", null);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
