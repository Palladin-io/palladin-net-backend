using System.Net;
using Palladin.Core.Types;
using Palladin.Module.Agents.Domain;
using Palladin.Module.Agents.Features;
using Palladin.Module.Agents.Infrastructure.AgentAuth;
using Palladin.Module.Agents.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Seeders;
using FastEndpoints;
using FastEndpoints.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Agents;

[Collection<ApiFactoryCollection>]
public sealed class GetAgentMeTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_AgentIsActive_Then_ReturnsProfile()
    {
        // Given
        var (_, organization, _) = await apiFactory.Services.SeedUserAsync();
        var (_, plaintext) = await apiFactory.Services.SeedApiKeyAsync(organization.Id);

        var publicKey = AgentFaker.GeneratePublicKey();
        var signing = AgentRequestSigning.Generate();
        var agent = await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(
                    organizationId: organization.Id,
                    publicKey: publicKey,
                    signingPublicKey: signing.PublicKeyBase64)
                .RuleFor(x => x.Status, AgentStatus.Active));

        var client = apiFactory.CreateSignedAgentClient(agent.Id, plaintext, publicKey, signing);

        // When
        var (response, result) = await client
            .GETAsync<GetAgentMeEndpoint, GetAgentMeResponse>();

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.ShouldNotBeNull();
        result.AgentId.ShouldBe(agent.Id);
        result.OrganizationId.ShouldBe(organization.Id);
        result.Status.ShouldBe(AgentStatus.Active);
    }

    [Fact]
    public async Task When_ActiveAgentConnectsWithoutSignature_Then_Returns401()
    {
        // Given
        var (_, organization, _) = await apiFactory.Services.SeedUserAsync();
        var (_, plaintext) = await apiFactory.Services.SeedApiKeyAsync(organization.Id);

        var publicKey = AgentFaker.GeneratePublicKey();
        var signing = AgentRequestSigning.Generate();
        await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(
                    organizationId: organization.Id,
                    publicKey: publicKey,
                    signingPublicKey: signing.PublicKeyBase64)
                .RuleFor(x => x.Status, AgentStatus.Active));

        var client = apiFactory.CreateClient();
        client.DefaultRequestHeaders.Add(AgentAuthenticationOptions.ApiKeyHeader, plaintext);
        client.DefaultRequestHeaders.Add(AgentAuthenticationOptions.AgentKeyHeader, publicKey);

        // When — no Ed25519 signature headers on the connect probe
        var response = await client.GetAsync("api/agent/me", TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task When_AgentIsPending_Then_Returns401()
    {
        // Given
        var (_, organization, _) = await apiFactory.Services.SeedUserAsync();
        var (_, plaintext) = await apiFactory.Services.SeedApiKeyAsync(organization.Id);

        var publicKey = AgentFaker.GeneratePublicKey();
        await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(organizationId: organization.Id, publicKey: publicKey)
                .RuleFor(x => x.Status, AgentStatus.Pending)
                .RuleFor(x => x.AccessEpoch, 0u));

        var client = apiFactory.CreateClient();
        client.DefaultRequestHeaders.Add(AgentAuthenticationOptions.ApiKeyHeader, plaintext);
        client.DefaultRequestHeaders.Add(AgentAuthenticationOptions.AgentKeyHeader, publicKey);

        // When
        var response = await client.GetAsync("api/agent/me", TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task When_InvalidApiKey_Then_Returns401()
    {
        // Given
        var (_, organization, _) = await apiFactory.Services.SeedUserAsync();

        var publicKey = AgentFaker.GeneratePublicKey();
        await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(organizationId: organization.Id, publicKey: publicKey)
                .RuleFor(x => x.Status, AgentStatus.Active));

        var client = apiFactory.CreateClient();
        client.DefaultRequestHeaders.Add(AgentAuthenticationOptions.ApiKeyHeader, "pl_not-a-real-key");
        client.DefaultRequestHeaders.Add(AgentAuthenticationOptions.AgentKeyHeader, publicKey);

        // When
        var response = await client.GetAsync("api/agent/me", TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task When_RevokedApiKey_Then_Returns401()
    {
        // Given
        var (_, organization, _) = await apiFactory.Services.SeedUserAsync();
        var (_, plaintext) = await apiFactory.Services.SeedApiKeyAsync(
            organization.Id,
            revokedAt: SystemClock.Instance.GetCurrentInstant());

        var publicKey = AgentFaker.GeneratePublicKey();
        await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(organizationId: organization.Id, publicKey: publicKey)
                .RuleFor(x => x.Status, AgentStatus.Active));

        var client = apiFactory.CreateClient();
        client.DefaultRequestHeaders.Add(AgentAuthenticationOptions.ApiKeyHeader, plaintext);
        client.DefaultRequestHeaders.Add(AgentAuthenticationOptions.AgentKeyHeader, publicKey);

        // When
        var response = await client.GetAsync("api/agent/me", TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task When_UnknownAgentKey_Then_EnrollsPendingAgentAndReturns401()
    {
        // Given
        var (_, organization, _) = await apiFactory.Services.SeedUserAsync();
        var (_, plaintext) = await apiFactory.Services.SeedApiKeyAsync(organization.Id);

        var client = apiFactory.CreateClient();
        client.DefaultRequestHeaders.Add(AgentAuthenticationOptions.ApiKeyHeader, plaintext);
        client.DefaultRequestHeaders.Add(AgentAuthenticationOptions.AgentKeyHeader, AgentFaker.GeneratePublicKey());
        client.DefaultRequestHeaders.Add(
            AgentAuthenticationOptions.AgentSigningKeyHeader, AgentRequestSigning.Generate().PublicKeyBase64);

        // When
        var response = await client.GetAsync("api/agent/me", TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.ShouldContain(h => h.Key == AgentAuthenticationOptions.AgentIdHeader);
    }

    [Fact]
    public async Task When_AgentKeyIsNotExactlyOneX25519PublicKey_Then_DoesNotEnroll()
    {
        // Given
        var (_, organization, _) = await apiFactory.Services.SeedUserAsync();
        var (_, plaintext) = await apiFactory.Services.SeedApiKeyAsync(organization.Id);
        var invalidPublicKey = Convert.ToBase64String(new byte[33]);
        var client = apiFactory.CreateClient();
        client.DefaultRequestHeaders.Add(AgentAuthenticationOptions.ApiKeyHeader, plaintext);
        client.DefaultRequestHeaders.Add(AgentAuthenticationOptions.AgentKeyHeader, invalidPublicKey);
        client.DefaultRequestHeaders.Add(
            AgentAuthenticationOptions.AgentSigningKeyHeader, AgentRequestSigning.Generate().PublicKeyBase64);

        // When
        var response = await client.GetAsync("api/agent/me", TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await CountAgentsAsync(organization.Id, invalidPublicKey)).ShouldBe(0);
    }

    [Fact]
    public async Task When_EnrollsWithoutSigningKey_Then_NotEnrolled()
    {
        // Given
        var (_, organization, _) = await apiFactory.Services.SeedUserAsync();
        var (_, plaintext) = await apiFactory.Services.SeedApiKeyAsync(organization.Id);
        var publicKey = AgentFaker.GeneratePublicKey();

        var client = apiFactory.CreateClient();
        client.DefaultRequestHeaders.Add(AgentAuthenticationOptions.ApiKeyHeader, plaintext);
        client.DefaultRequestHeaders.Add(AgentAuthenticationOptions.AgentKeyHeader, publicKey);

        // When
        var response = await client.GetAsync("api/agent/me", TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await CountAgentsAsync(organization.Id, publicKey)).ShouldBe(0);
    }

    [Fact]
    public async Task When_EnrollsWithAgentTypeHeader_Then_PersistsType()
    {
        // Given
        var (_, organization, _) = await apiFactory.Services.SeedUserAsync();
        var (_, plaintext) = await apiFactory.Services.SeedApiKeyAsync(organization.Id);
        var publicKey = AgentFaker.GeneratePublicKey();

        var client = apiFactory.CreateClient();
        client.DefaultRequestHeaders.Add(AgentAuthenticationOptions.ApiKeyHeader, plaintext);
        client.DefaultRequestHeaders.Add(AgentAuthenticationOptions.AgentKeyHeader, publicKey);
        client.DefaultRequestHeaders.Add(
            AgentAuthenticationOptions.AgentSigningKeyHeader, AgentRequestSigning.Generate().PublicKeyBase64);
        client.DefaultRequestHeaders.Add(AgentAuthenticationOptions.AgentTypeHeader, "scraper");

        // When
        var response = await client.GetAsync("api/agent/me", TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var type = await ReadAgentTypeAsync(organization.Id, publicKey);
        type.ShouldBe("scraper");
    }

    [Fact]
    public async Task When_EnrollsWithoutAgentTypeHeader_Then_PersistsNoType()
    {
        // Given
        var (_, organization, _) = await apiFactory.Services.SeedUserAsync();
        var (_, plaintext) = await apiFactory.Services.SeedApiKeyAsync(organization.Id);
        var publicKey = AgentFaker.GeneratePublicKey();

        var client = apiFactory.CreateClient();
        client.DefaultRequestHeaders.Add(AgentAuthenticationOptions.ApiKeyHeader, plaintext);
        client.DefaultRequestHeaders.Add(AgentAuthenticationOptions.AgentKeyHeader, publicKey);
        client.DefaultRequestHeaders.Add(
            AgentAuthenticationOptions.AgentSigningKeyHeader, AgentRequestSigning.Generate().PublicKeyBase64);

        // When
        var response = await client.GetAsync("api/agent/me", TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var type = await ReadAgentTypeAsync(organization.Id, publicKey);
        type.ShouldBeNull();
    }

    [Fact]
    public async Task When_ActiveAgentReconnectsWithDifferentType_Then_PreservesApprovedType()
    {
        // Given
        var (_, organization, _) = await apiFactory.Services.SeedUserAsync();
        var (_, plaintext) = await apiFactory.Services.SeedApiKeyAsync(organization.Id);
        var publicKey = AgentFaker.GeneratePublicKey();
        var signing = AgentRequestSigning.Generate();
        var agent = await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(
                    organizationId: organization.Id,
                    publicKey: publicKey,
                    signingPublicKey: signing.PublicKeyBase64)
                .RuleFor(x => x.Status, AgentStatus.Active)
                .RuleFor(x => x.Type, "scraper"));

        var client = apiFactory.CreateSignedAgentClient(agent.Id, plaintext, publicKey, signing);
        client.DefaultRequestHeaders.Add(AgentAuthenticationOptions.AgentTypeHeader, "archiver");

        // When
        var response = await client.GetAsync("api/agent/me", TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var type = await ReadAgentTypeAsync(organization.Id, publicKey);
        type.ShouldBe("scraper");
    }

    [Fact]
    public async Task When_ReconnectsWithoutAgentTypeHeader_Then_PreservesExistingType()
    {
        // Given
        var (_, organization, _) = await apiFactory.Services.SeedUserAsync();
        var (_, plaintext) = await apiFactory.Services.SeedApiKeyAsync(organization.Id);
        var publicKey = AgentFaker.GeneratePublicKey();
        var signing = AgentRequestSigning.Generate();
        var agent = await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(
                    organizationId: organization.Id,
                    publicKey: publicKey,
                    signingPublicKey: signing.PublicKeyBase64)
                .RuleFor(x => x.Status, AgentStatus.Active)
                .RuleFor(x => x.Type, "ci"));

        var client = apiFactory.CreateSignedAgentClient(agent.Id, plaintext, publicKey, signing);

        // When
        var response = await client.GetAsync("api/agent/me", TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var type = await ReadAgentTypeAsync(organization.Id, publicKey);
        type.ShouldBe("ci");
    }

    [Fact]
    public async Task When_SamePublicKeyEnrolledTwice_Then_NoDuplicateAgent()
    {
        // Given
        var (_, organization, _) = await apiFactory.Services.SeedUserAsync();
        var (_, plaintext) = await apiFactory.Services.SeedApiKeyAsync(organization.Id);
        var publicKey = AgentFaker.GeneratePublicKey();

        var client = apiFactory.CreateClient();
        client.DefaultRequestHeaders.Add(AgentAuthenticationOptions.ApiKeyHeader, plaintext);
        client.DefaultRequestHeaders.Add(AgentAuthenticationOptions.AgentKeyHeader, publicKey);
        client.DefaultRequestHeaders.Add(
            AgentAuthenticationOptions.AgentSigningKeyHeader, AgentRequestSigning.Generate().PublicKeyBase64);

        // When — two enrollment attempts with the same public key
        await client.GetAsync("api/agent/me", TestContext.Current.CancellationToken);
        await client.GetAsync("api/agent/me", TestContext.Current.CancellationToken);

        // Then — the second resolves to the existing agent, no duplicate row
        (await CountAgentsByPublicKeyAsync(publicKey)).ShouldBe(1);
    }

    [Fact]
    public async Task When_PublicKeyRegisteredToAnotherOrganization_Then_RejectedWithoutDuplicate()
    {
        // Given — a public key already enrolled under organization A
        var (_, organizationA, _) = await apiFactory.Services.SeedUserAsync();
        var publicKey = AgentFaker.GeneratePublicKey();
        await apiFactory.Services.SeedAgentAsync(
            organizationA.Id,
            AgentFaker.Create(organizationId: organizationA.Id, publicKey: publicKey)
                .RuleFor(x => x.Status, AgentStatus.Active));

        // and organization B presenting the same public key
        var (_, organizationB, _) = await apiFactory.Services.SeedUserAsync();
        var (_, plaintextB) = await apiFactory.Services.SeedApiKeyAsync(organizationB.Id);

        var client = apiFactory.CreateClient();
        client.DefaultRequestHeaders.Add(AgentAuthenticationOptions.ApiKeyHeader, plaintextB);
        client.DefaultRequestHeaders.Add(AgentAuthenticationOptions.AgentKeyHeader, publicKey);
        client.DefaultRequestHeaders.Add(
            AgentAuthenticationOptions.AgentSigningKeyHeader, AgentRequestSigning.Generate().PublicKeyBase64);

        // When
        var response = await client.GetAsync("api/agent/me", TestContext.Current.CancellationToken);

        // Then — rejected, and no agent created under organization B
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await CountAgentsByPublicKeyAsync(publicKey)).ShouldBe(1);
        (await CountAgentsAsync(organizationB.Id, publicKey)).ShouldBe(0);
    }

    [Fact]
    public async Task When_SamePublicKeyEnrolledConcurrentlyFromTwoOrganizations_Then_NoForeignAgentResolved()
    {
        // Given — two organizations racing to enroll the SAME public key
        var (_, organizationA, _) = await apiFactory.Services.SeedUserAsync();
        var (_, plaintextA) = await apiFactory.Services.SeedApiKeyAsync(organizationA.Id);
        var (_, organizationB, _) = await apiFactory.Services.SeedUserAsync();
        var (_, plaintextB) = await apiFactory.Services.SeedApiKeyAsync(organizationB.Id);
        var publicKey = AgentFaker.GeneratePublicKey();

        var clientA = EnrollmentClient(plaintextA, publicKey);
        var clientB = EnrollmentClient(plaintextB, publicKey);

        // When — both enrollments fire concurrently
        var responses = await Task.WhenAll(
            clientA.GetAsync("api/agent/me", TestContext.Current.CancellationToken),
            clientB.GetAsync("api/agent/me", TestContext.Current.CancellationToken));

        // Then — the unique key allows exactly one agent, owned by a single organization; the losing
        // organization never has the key resolved to it (no foreign agent, no duplicate).
        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.Unauthorized);
        (await CountAgentsByPublicKeyAsync(publicKey)).ShouldBe(1);

        var inA = await CountAgentsAsync(organizationA.Id, publicKey);
        var inB = await CountAgentsAsync(organizationB.Id, publicKey);
        (inA + inB).ShouldBe(1);
        (inA == 0 || inB == 0).ShouldBeTrue();
    }

    [Fact]
    public async Task When_DistinctNamesEnrollConcurrently_Then_BothNamesArePreserved()
    {
        var (_, organization, _) = await apiFactory.Services.SeedUserAsync();
        var (_, plaintext) = await apiFactory.Services.SeedApiKeyAsync(organization.Id);
        using var first = EnrollmentClient(plaintext, AgentFaker.GeneratePublicKey());
        using var second = EnrollmentClient(plaintext, AgentFaker.GeneratePublicKey());
        first.DefaultRequestHeaders.Add(AgentAuthenticationOptions.AgentNameHeader, "Amber Fox");
        second.DefaultRequestHeaders.Add(AgentAuthenticationOptions.AgentNameHeader, "Quiet Otter");

        var responses = await Task.WhenAll(
            first.GetAsync("api/agent/me", TestContext.Current.CancellationToken),
            second.GetAsync("api/agent/me", TestContext.Current.CancellationToken));

        responses.ShouldAllBe(response => response.StatusCode == HttpStatusCode.Unauthorized);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AgentsDbReadContext>();
        var names = await context.Agents.Where(agent => agent.OrganizationId == organization.Id)
            .Select(agent => agent.Name).ToListAsync(TestContext.Current.CancellationToken);
        names.Order().ShouldBe(new[] { "Amber Fox", "Quiet Otter" });
    }

    private HttpClient EnrollmentClient(string apiKey, string publicKey)
    {
        var client = apiFactory.CreateClient();
        client.DefaultRequestHeaders.Add(AgentAuthenticationOptions.ApiKeyHeader, apiKey);
        client.DefaultRequestHeaders.Add(AgentAuthenticationOptions.AgentKeyHeader, publicKey);
        client.DefaultRequestHeaders.Add(
            AgentAuthenticationOptions.AgentSigningKeyHeader, AgentRequestSigning.Generate().PublicKeyBase64);
        return client;
    }

    private async Task<int> CountAgentsByPublicKeyAsync(string publicKey)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<AgentsDbReadContext>();
        return await readContext.Agents
            .CountAsync(a => a.PublicKey == publicKey, TestContext.Current.CancellationToken);
    }

    private async Task<int> CountAgentsAsync(Guid organizationId, string publicKey)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<AgentsDbReadContext>();
        return await readContext.Agents
            .CountAsync(a => a.OrganizationId == organizationId && a.PublicKey == publicKey, TestContext.Current.CancellationToken);
    }

    private async Task<string?> ReadAgentTypeAsync(Guid organizationId, string publicKey)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<AgentsDbReadContext>();
        return await readContext.Agents
            .Where(a => a.OrganizationId == organizationId && a.PublicKey == publicKey)
            .Select(a => a.Type)
            .FirstAsync(TestContext.Current.CancellationToken);
    }
}
