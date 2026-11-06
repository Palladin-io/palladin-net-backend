using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Palladin.Core.Cryptography;
using Palladin.Core.Security;
using Palladin.Module.Vault.Domain;
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
    int AccountMemberEnvelopesPendingRewrap,
    Guid[] OrganizationIdsPendingRewrap,
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
                + "when accountMemberEnvelopesPendingRewrap is zero: the Member key is account-wide, so that count and "
                + "organizationIdsPendingRewrap cover every organization; the client re-wraps after switching to each.";
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

        var envelopes = MemberKeyMigration.CurrentEnvelopes(domainReadContext, userId);
        var memberEnvelopes = await envelopes.CountAsync(x => x.OrganizationId == organizationId, ct);
        var pending = MemberKeyMigration.PendingRewrap(envelopes, memberKey?.KeyVersion, memberKey?.PublicKeySuiteId);
        var pendingRewrap = await pending.CountAsync(x => x.OrganizationId == organizationId, ct);
        var pendingByOrganization = await pending
            .GroupBy(x => x.OrganizationId)
            .Select(x => new { OrganizationId = x.Key, Count = x.Count() })
            .ToListAsync(ct);

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
            pendingByOrganization.Sum(x => x.Count),
            pendingByOrganization.Select(x => x.OrganizationId).Order().ToArray(),
            agentDiscoveryEnvelopes + agentVaultKeys,
            vaultMessageKeys,
            approverMessageKeys), ct);
    }
}

internal static class MemberKeyMigration
{
    // Envelopes of a superseded Member-key generation can never be used again, so only the Vault's current
    // generation counts, in every organization the caller belongs to.
    internal static IQueryable<VaultMemberKeyEnvelope> CurrentEnvelopes(VaultDomainReadContext context, Guid userId) =>
        from envelope in context.VaultMemberKeyEnvelopes
        join vault in context.Vaults
            on new { envelope.OrganizationId, envelope.VaultId } equals new { vault.OrganizationId, VaultId = vault.Id }
        where envelope.MemberId == userId && envelope.MemberKeyGeneration == vault.MemberKeyGeneration
        select envelope;

    internal static IQueryable<VaultMemberKeyEnvelope> PendingRewrap(
        IQueryable<VaultMemberKeyEnvelope> envelopes, MemberRecipientKeyVersion? keyVersion, string? suiteId)
    {
        if (keyVersion is not { } version)
        {
            return envelopes;
        }

        return envelopes.Where(x => x.RecipientKeyVersion != version || x.WrapperSuiteId != suiteId);
    }
}
