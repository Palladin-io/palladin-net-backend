using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Palladin.Core.Cryptography;
using Palladin.Core.Security;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record KeyMigrationMemberKeyContract(uint KeyVersion, string SuiteId);

[PublicAPI]
public sealed record KeyMigrationReportResponse(
    string CurrentSuiteId,
    KeyMigrationMemberKeyContract? MemberKey,
    int MemberEnvelopes,
    int MemberEnvelopesPendingRewrap,
    int AgentEnvelopesOnNonCurrentSuite,
    int VaultAgentMessageKeysOnNonCurrentSuite,
    int ApproverMessageKeysOnNonCurrentSuite);

[PublicAPI]
internal sealed class GetKeyMigrationReportEndpoint(VaultDomainReadContext domainReadContext)
    : EndpointWithoutRequest<KeyMigrationReportResponse>
{
    public override void Configure()
    {
        Get("api/account/key-migration");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Summary(summary =>
        {
            summary.Summary = "Report key-wrapper migration progress for the caller";
            summary.Description = "Counts the caller's current-generation Vault-key envelopes in the active organization that are not "
                + "addressed to their current Member key and key-wrapper suite, plus Agent-addressed envelopes in "
                + "the caller's Vaults that still use a non-current suite, Vault agent-message keys (legacy manifests) and the "
                + "organization approver message key on a non-current suite. The legacy Member key may be deleted only "
                + "when memberEnvelopesPendingRewrap is zero.";
        });
        Tags("Vault/KeyMigration");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var userId = User.GetUserId()!.Value;
        var organizationId = User.GetOrganizationId()!.Value;
        var currentSuiteId = KeyWrapperSuites.Current.Id;
        var memberKey = await domainReadContext.MemberKeyDirectory
            .Where(x => x.UserId == userId)
            .Select(x => new { x.KeyVersion, x.PublicKeySuiteId })
            .SingleOrDefaultAsync(ct);

        var envelopes =
            from envelope in domainReadContext.VaultMemberKeyEnvelopes
            join vault in domainReadContext.Vaults
                on new { envelope.OrganizationId, envelope.VaultId } equals new { vault.OrganizationId, VaultId = vault.Id }
            where envelope.OrganizationId == organizationId && envelope.MemberId == userId
                  && envelope.MemberKeyGeneration == vault.MemberKeyGeneration
            select envelope;
        var memberEnvelopes = await envelopes.CountAsync(ct);
        var pendingRewrap = memberKey is null
            ? memberEnvelopes
            : await envelopes.CountAsync(x =>
                x.RecipientKeyVersion != memberKey.KeyVersion || x.WrapperSuiteId != memberKey.PublicKeySuiteId, ct);

        var memberVaultIds = domainReadContext.VaultMembers
            .Where(x => x.OrganizationId == organizationId && x.UserId == userId)
            .Select(x => x.VaultId);
        var agentDiscoveryEnvelopes = await domainReadContext.AgentVaultDiscoveryEnvelopes
            .CountAsync(x => x.OrganizationId == organizationId && memberVaultIds.Contains(x.VaultId)
                             && x.RevokedAt == null && x.WrapperSuiteId != currentSuiteId, ct);
        var agentVaultKeys = await domainReadContext.AgentWrappedVaultKeys
            .CountAsync(x => x.OrganizationId == organizationId && memberVaultIds.Contains(x.VaultId)
                             && x.WrapperSuiteId != currentSuiteId, ct);

        var vaultMessageKeys = await domainReadContext.Vaults.CountAsync(x =>
            x.OrganizationId == organizationId && memberVaultIds.Contains(x.Id)
            && x.AgentMessageKeySuiteId != currentSuiteId, ct);
        var approverMessageKeys = await domainReadContext.OrganizationApproverKeySets.CountAsync(x =>
            x.OrganizationId == organizationId && x.MessageKeySuiteId != currentSuiteId, ct);

        await Send.OkAsync(new KeyMigrationReportResponse(
            currentSuiteId,
            memberKey is null ? null : new KeyMigrationMemberKeyContract(memberKey.KeyVersion.Value, memberKey.PublicKeySuiteId),
            memberEnvelopes,
            pendingRewrap,
            agentDiscoveryEnvelopes + agentVaultKeys,
            vaultMessageKeys,
            approverMessageKeys), ct);
    }
}
