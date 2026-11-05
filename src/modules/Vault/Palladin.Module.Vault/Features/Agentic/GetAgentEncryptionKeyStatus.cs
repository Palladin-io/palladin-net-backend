using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Palladin.Core.Types;
using Palladin.Module.Agents.Features;
using Palladin.Module.Agents.Infrastructure.AgentAuth;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
internal sealed class GetAgentEncryptionKeyStatusEndpoint(VaultDomainReadContext domainReadContext)
    : EndpointWithoutRequest<AgentEncryptionKeyStatusResponse>
{
    public override void Configure()
    {
        Get("api/agent/encryption-key");
        AuthSchemes(AgentAuthenticationOptions.SchemeName);
        Summary(summary =>
        {
            summary.Summary = "Report the authenticated Agent's encryption key and old-key envelopes";
            summary.Description = "Returns the encryption key publication Vault currently seals to and the number of "
                + "live envelopes (discovery VDK, grant DEK, Agent Vault key, script package) still addressed to any "
                + "non-current Agent key version. The Agent may delete its previous private key only at zero.";
        });
        Tags("Vault/Agents");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var agentId = User.GetAgentId()!.Value;
        var agent = await domainReadContext.Agents
            .SingleOrDefaultAsync(x => x.Id == agentId && x.Status == AgentStatus.Active, ct);
        if (agent is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var organizationId = agent.OrganizationId;
        var version = agent.RecipientKeyVersion;
        var current = new AgentRecipientKeyVersion(version);
        var discovery = await domainReadContext.AgentVaultDiscoveryEnvelopes.CountAsync(x =>
            x.OrganizationId == organizationId && x.AgentId == agentId && x.RevokedAt == null
            && x.RecipientAgentKeyVersion != current, ct);
        var vaultKeys = await domainReadContext.AgentWrappedVaultKeys.CountAsync(x =>
            x.OrganizationId == organizationId && x.AgentId == agentId
            && x.RecipientAgentKeyVersion != current, ct);
        var grantEnvelopes = await (
            from envelope in domainReadContext.GrantEntryEnvelopes
            join grant in domainReadContext.Grants on envelope.GrantId equals grant.Id
            where grant.OrganizationId == organizationId && grant.AgentId == agentId
                  && grant.Status == GrantStatus.Active
                  && envelope.RecipientAgentKeyVersion != current
            select envelope).CountAsync(ct);
        var scriptPackages = await domainReadContext.ScriptExecutionPackages.CountAsync(x =>
            x.OrganizationId == organizationId && x.AgentId == agentId && x.RecipientAgentKeyVersion != version, ct);

        await Send.OkAsync(new AgentEncryptionKeyStatusResponse(
            agent.PublicKeySuiteId,
            version,
            WebEncoders.Base64UrlEncode(AgentRecipientKey.Fingerprint(agent)),
            (ulong)(discovery + vaultKeys + grantEnvelopes + scriptPackages)), ct);
    }
}
