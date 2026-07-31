using System.Net;
using System.Security.Cryptography;
using System.Text;
using Palladin.Core.Types;
using Palladin.Module.Agents.Domain;
using Palladin.Module.Agents.Infrastructure.AgentAuth;
using Palladin.Module.Agents.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Seeders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Agents;

[Collection<ApiFactoryCollection>]
public sealed class AgentSignatureTests(ApiFactory apiFactory) : TestBase
{
    private const string SignedEndpoint = "api/agent/entries?query=test";

    [Fact]
    public async Task When_RequestProperlySigned_Then_Authorized()
    {
        // Given
        var (agentId, plaintext, publicKey, signing) = await SeedSignedAgentAsync();
        var client = apiFactory.CreateSignedAgentClient(agentId, plaintext, publicKey, signing);

        // When
        var response = await client.GetAsync(SignedEndpoint, TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task When_SignatureHeadersMissing_Then_Unauthorized()
    {
        // Given
        var (_, plaintext, publicKey, _) = await SeedSignedAgentAsync();
        var client = apiFactory.CreateClient();
        client.DefaultRequestHeaders.Add(AgentAuthenticationOptions.ApiKeyHeader, plaintext);
        client.DefaultRequestHeaders.Add(AgentAuthenticationOptions.AgentKeyHeader, publicKey);

        // When
        var response = await client.GetAsync(SignedEndpoint, TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task When_SignedByWrongKey_Then_Unauthorized()
    {
        // Given
        var (agentId, plaintext, publicKey, _) = await SeedSignedAgentAsync();
        var attackerSigning = AgentRequestSigning.Generate();
        var client = apiFactory.CreateSignedAgentClient(agentId, plaintext, publicKey, attackerSigning);

        // When
        var response = await client.GetAsync(SignedEndpoint, TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task When_NonceReplayed_Then_SecondRequestUnauthorized()
    {
        // Given
        var (agentId, plaintext, publicKey, signing) = await SeedSignedAgentAsync();
        var (timestamp, nonce, signature) = SignGet(signing, "/" + SignedEndpoint);

        // When
        var first = await SendRawSignedAsync(agentId, plaintext, publicKey, timestamp, nonce, signature);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Then
        var second = await SendRawSignedAsync(agentId, plaintext, publicKey, timestamp, nonce, signature);
        second.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task When_TimestampOutsideWindow_Then_Unauthorized()
    {
        // Given
        var (agentId, plaintext, publicKey, signing) = await SeedSignedAgentAsync();
        var staleTimestamp = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 3600).ToString();
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var canonical = string.Join('\n', "GET", "/" + SignedEndpoint, staleTimestamp, nonce,
            Convert.ToBase64String(SHA256.HashData([])));
        var signature = signing.SignCanonical(canonical);

        // When
        var response = await SendRawSignedAsync(agentId, plaintext, publicKey, staleTimestamp, nonce, signature);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task When_EnrollsWithSigningKeyHeader_Then_Persisted()
    {
        // Given
        var organizationId = Guid.NewGuid();
        var (_, plaintext) = await apiFactory.Services.SeedApiKeyAsync(organizationId);
        var publicKey = AgentFaker.GeneratePublicKey();
        var signing = AgentRequestSigning.Generate();

        var client = apiFactory.CreateClient();
        client.DefaultRequestHeaders.Add(AgentAuthenticationOptions.ApiKeyHeader, plaintext);
        client.DefaultRequestHeaders.Add(AgentAuthenticationOptions.AgentKeyHeader, publicKey);
        client.DefaultRequestHeaders.Add(AgentAuthenticationOptions.AgentSigningKeyHeader, signing.PublicKeyBase64);

        // When
        var response = await client.GetAsync("api/agent/me", TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<AgentsDbReadContext>();
        var stored = await readContext.Agents
            .Where(a => a.OrganizationId == organizationId && a.PublicKey == publicKey)
            .Select(a => a.SigningPublicKey)
            .FirstAsync(TestContext.Current.CancellationToken);
        stored.ShouldBe(signing.PublicKeyBase64);
    }

    private async Task<(Guid AgentId, string Plaintext, string PublicKey, AgentRequestSigning Signing)> SeedSignedAgentAsync()
    {
        var organizationId = Guid.NewGuid();
        var (_, plaintext) = await apiFactory.Services.SeedApiKeyAsync(organizationId);
        var publicKey = AgentFaker.GeneratePublicKey();
        var signing = AgentRequestSigning.Generate();
        var agent = await apiFactory.Services.SeedAgentAsync(
            organizationId,
            AgentFaker.Create(
                    organizationId: organizationId,
                    publicKey: publicKey,
                    signingPublicKey: signing.PublicKeyBase64)
                .RuleFor(x => x.Status, AgentStatus.Active));
        await apiFactory.Services.SeedVaultAgentAsync(organizationId, status: AgentStatus.Active, id: agent.Id);
        return (agent.Id, plaintext, publicKey, signing);
    }

    private static (string Timestamp, string Nonce, string Signature) SignGet(AgentRequestSigning signing, string pathWithQuery)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var canonical = string.Join('\n', "GET", pathWithQuery, timestamp, nonce,
            Convert.ToBase64String(SHA256.HashData([])));
        return (timestamp, nonce, signing.SignCanonical(canonical));
    }

    private async Task<HttpResponseMessage> SendRawSignedAsync(
        Guid agentId, string plaintext, string publicKey, string timestamp, string nonce, string signature)
    {
        var client = apiFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, SignedEndpoint);
        request.Headers.Add(AgentAuthenticationOptions.ApiKeyHeader, plaintext);
        request.Headers.Add(AgentAuthenticationOptions.AgentKeyHeader, publicKey);
        request.Headers.Add("X-Agent-Id", agentId.ToString());
        request.Headers.Add("X-Agent-Timestamp", timestamp);
        request.Headers.Add("X-Agent-Nonce", nonce);
        request.Headers.Add("X-Agent-Signature", signature);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
