using Palladin.Module.Agents.Infrastructure.AgentAuth;

namespace Palladin.Tests.Integrations.Shared.Extensions;

internal static class AgentExtensions
{
    public static HttpClient CreateSignedAgentClient(
        this ApiFactory apiFactory,
        Guid agentId,
        string apiKeyPlaintext,
        string boxPublicKey,
        AgentRequestSigning signing)
    {
        var inner = apiFactory.CreateHandler();
        var client = new HttpClient(signing.CreateSigningHandler(agentId, inner))
        {
            BaseAddress = apiFactory.Client.BaseAddress,
        };

        client.DefaultRequestHeaders.Add(AgentAuthenticationOptions.ApiKeyHeader, apiKeyPlaintext);
        client.DefaultRequestHeaders.Add(AgentAuthenticationOptions.AgentKeyHeader, boxPublicKey);

        return client;
    }
}
