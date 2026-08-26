using Palladin.Core.Security;
using Palladin.Core.Json;
using Palladin.Core.Types;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Authorization;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Crypto;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.WebUtilities;
using NodaTime;
using System.Text.Json.Serialization;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record GetGrantRequest : IRequiresVaultMembership
{
    public Guid VaultId { get; init; }
    public Guid GrantId { get; init; }
}

// User-auth responses may carry the encrypted reason envelope so an unlocked Vault Member can
// decrypt the Agent's request locally. They never carry credential grant payload ciphertext,
// plaintext reason, secret values, or private/unwrapped key material.
// AgentName and actor names are resolved server-side from non-vault replicas. EntryLabel and
// UrlDomain remain null because vault-entry metadata is encrypted and interpreted only by clients.
// AgentPublicKey is the agent's X25519 PUBLIC key (base64) — NOT a secret. The approving user's
// client needs it to wrap the GrantDEK on approval.
[PublicAPI]
public sealed record GrantResponse(
    Guid Id,
    Guid VaultId,
    Guid? AgentId,
    uint? AgentAccessEpoch,
    string? AgentName,
    string? AgentIconKey,
    string? AgentPublicKey,
    uint? RecipientAgentKeyVersion,
    string? AgentSigningPublicKey,
    uint? AgentSigningKeyVersion,
    string? AgentSigningKeyFingerprint,
    GrantType Type,
    GrantStatus Status,
    GrantMethods Methods,
    Guid? EntryId,
    string? EntryLabel,
    string? UrlDomain,
    IReadOnlyList<GrantEntryScopeResponse> EntryScopes,
    EncryptedReasonEnvelopeContract? EncryptedReason,
    Instant? ExpiresAt,
    int? QueryLimit,
    int QueryCount,
    string ExpirySource,
    Instant CreatedAt,
    Guid? CreatedBy,
    string? CreatedByName,
    Instant? RevokedAt,
    Guid? RevokedBy,
    string? RevokedByName,
    Instant? SupersededAt,
    Guid? SupersededByGrantId,
    Instant? DeniedAt,
    Guid? DeniedBy,
    string? DeniedByName,
    Instant? LastAccessedAt,
    string? LastAccessIp,
    string? LastAccessHostname,
    bool CanRevoke,
    bool CanGrantAgain,
    IReadOnlyList<Guid> ActiveCoveringGrantIds);

[PublicAPI]
public sealed record GrantEntryScopeResponse(
    Guid EntryId,
    string[] FieldIds,
    string? GrantEnvelopeRevision,
    string? EntryRevision,
    uint? GrantKeyVersion,
    uint? MemberKeyGeneration,
    uint? RecipientAgentKeyVersion,
    [property: JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] byte[]? AgentKeyFingerprint);

[UsedImplicitly]
internal sealed class GetGrantValidator : Validator<GetGrantRequest>
{
    public GetGrantValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.GrantId).NotEmpty();
    }
}

[PublicAPI]
internal sealed class GetGrantEndpoint(VaultDomainReadContext domainReadContext) : Endpoint<GetGrantRequest, GrantResponse>
{
    public override void Configure()
    {
        Get("api/vaults/{vaultId:guid}/grants/{grantId:guid}");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.GrantManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(summary =>
        {
            summary.Summary = "Get a single grant";
            summary.Description = "Returns grant metadata and the encrypted Agent reason envelope for local Member decryption. Credential grant payload ciphertext, plaintext secrets and private or unwrapped keys are never included.";
        });
        Tags("Vault/Grants");
    }

    public override async Task HandleAsync(GetGrantRequest req, CancellationToken ct)
    {
        var grant = await domainReadContext.Grants
            .Where(g => g.Id == req.GrantId && g.VaultId == req.VaultId)
            .Select(GrantProjection.ToResponse(domainReadContext))
            .FirstOrDefaultAsync(ct);

        if (grant is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var encryptedReason = await domainReadContext.EncryptedReasonEnvelopes
            .Where(x => x.VaultId == req.VaultId && x.GrantRequestId == req.GrantId)
            .Select(x => new
            {
                x.ProtocolVersion, x.CryptoSuiteId, x.OrganizationId, x.VaultId, x.EntryId,
                x.GrantRequestId, x.AgentId, x.ResourceRevision, x.ReasonKeyVersion,
                x.MemberKeyGeneration, x.WrapperSuiteId, x.AgentMessageKeyVersion,
                x.RecipientAgentMessageKeyFingerprint, x.RequestedMethods, x.EncodedSuitePayload,
                x.AgentMessageWrappedReasonDek, x.AgentSignature,
            })
            .FirstOrDefaultAsync(ct);
        if (encryptedReason is not null)
        {
            var scope = new EnvelopeScopeContract(encryptedReason.OrganizationId, encryptedReason.VaultId,
                encryptedReason.EntryId, encryptedReason.GrantRequestId, encryptedReason.AgentId);
            var binding = new ReasonEnvelopeBindingContract(
                encryptedReason.WrapperSuiteId, encryptedReason.AgentMessageKeyVersion,
                WebEncoders.Base64UrlEncode(encryptedReason.RecipientAgentMessageKeyFingerprint),
                (ushort)encryptedReason.RequestedMethods);
            var descriptor = new EnvelopeDescriptorContract<ReasonEnvelopeBindingContract>(
                encryptedReason.ProtocolVersion, encryptedReason.CryptoSuiteId,
                EnvelopePurposeContract.EncryptedReason, scope,
                encryptedReason.ResourceRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                encryptedReason.ReasonKeyVersion, encryptedReason.MemberKeyGeneration, binding);
            var domainDescriptor = new EnvelopeDescriptor(
                descriptor.ProtocolVersion, new CryptoSuiteId(descriptor.CryptoSuiteId),
                EnvelopePurpose.EncryptedReason,
                new EnvelopeScope(scope.OrganizationId, scope.VaultId, scope.EntryId,
                    scope.GrantOrRequestId, scope.AgentId), encryptedReason.ResourceRevision,
                encryptedReason.ReasonKeyVersion, encryptedReason.MemberKeyGeneration,
                new ReasonEnvelopeBinding(binding.WrapperSuiteId, binding.RecipientKeyVersion,
                    encryptedReason.RecipientAgentMessageKeyFingerprint, binding.RequestedMethods));
            var parentHash = X25519WrapperContextCodec.ComputeParentDescriptorHash(domainDescriptor);
            grant = grant with
            {
                EncryptedReason = new EncryptedReasonEnvelopeContract(
                    descriptor,
                    WebEncoders.Base64UrlEncode(encryptedReason.EncodedSuitePayload),
                    new X25519WrappedKeyContract(
                        new X25519WrapperDescriptorContract(descriptor.ProtocolVersion,
                            binding.WrapperSuiteId, X25519WrapperPurposeContract.ReasonDek, scope,
                            descriptor.ResourceRevision, descriptor.KeyVersion, descriptor.MemberKeyGeneration,
                            X25519RecipientKeyKindContract.VaultMessageX25519,
                            binding.RecipientKeyVersion, binding.RecipientKeyFingerprint,
                            WebEncoders.Base64UrlEncode(parentHash)),
                        WebEncoders.Base64UrlEncode(encryptedReason.AgentMessageWrappedReasonDek)),
                    WebEncoders.Base64UrlEncode(encryptedReason.AgentSignature)),
            };
        }

        var grants = new List<GrantResponse> { grant };
        GrantProjection.ApplyAgentSigningIdentity(grants);
        await GrantProjection.ApplyCanGrantAgainAsync(domainReadContext, grants, ct);
        grant = grants[0];
        await Send.OkAsync(grant, ct);
    }
}

internal static class GrantProjection
{
    // System actor label for grant transitions performed without a user (e.g. cascade revoke on agent
    // deactivation) — surfaced instead of a bare/empty id so the UI reads "System".
    private const string SystemActor = "System";

    // AgentName and actor names (CreatedByName / RevokedByName / DeniedByName) are resolved via
    // correlated subqueries against the Agent and User replicas. EntryLabel and UrlDomain are
    // deliberately null: their canonical values live only inside client-decrypted projections.
    // EF Core translates these subqueries to LEFT JOIN LATERAL — a single query, no N+1.
    // FirstOrDefault yields null when the referenced row was deleted. RevokedByName is "System" when the
    // revoke was system-initiated (no RevokedBy). Takes the read context because the subqueries need its
    // DbSets; the result is still a translatable Expression.
    public static System.Linq.Expressions.Expression<Func<Grant, GrantResponse>> ToResponse(VaultDomainReadContext ctx)
    {
        return g => new GrantResponse(
            g.Id,
            g.VaultId,
            g.AgentId,
            g.AgentAccessEpoch,
            ctx.Agents.Where(a => a.Id == g.AgentId).Select(a => a.Name).FirstOrDefault(),
            ctx.Agents.Where(a => a.Id == g.AgentId).Select(a => a.IconKey).FirstOrDefault(),
            ctx.Agents.Where(a => a.Id == g.AgentId).Select(a => a.PublicKey).FirstOrDefault(),
            ctx.Agents.Where(a => a.Id == g.AgentId).Select(a => (uint?)a.RecipientKeyVersion).FirstOrDefault(),
            ctx.Agents.Where(a => a.Id == g.AgentId).Select(a => a.SigningPublicKey).FirstOrDefault(),
            ctx.Agents.Where(a => a.Id == g.AgentId).Select(a => (uint?)1).FirstOrDefault(),
            null,
            g is GranularGrant ? GrantType.Granular : GrantType.Full,
            g.Status,
            g.Methods,
            g is GranularGrant ? ((GranularGrant)g).EntryId : null,
            null,
            null,
            g.GrantEntryScopes.Select(scope => new GrantEntryScopeResponse(
                scope.EntryId,
                EF.Functions.StringToArray(scope.FieldIds, "\n"),
                scope.Envelope == null ? null : scope.Envelope.GrantEnvelopeRevision.ToString(),
                scope.Envelope == null ? null : scope.Envelope.EntryRevision.ToString(),
                scope.Envelope == null ? null : scope.Envelope.GrantKeyVersion,
                scope.Envelope == null ? null : scope.Envelope.MemberKeyGeneration.Value,
                scope.Envelope == null ? null : scope.Envelope.RecipientAgentKeyVersion.Value,
                scope.Envelope == null ? null : scope.Envelope.AgentKeyFingerprint)).ToArray(),
            null,
            g.ExpiresAt,
            g.QueryLimit,
            g.QueryCount,
            g.ExpirySource,
            g.CreatedAt,
            g.CreatedBy,
            g.CreatedBy == null
                ? null
                : ctx.Users.Where(u => u.Id == g.CreatedBy).Select(u => u.DisplayName).FirstOrDefault(),
            g.RevokedAt,
            g.RevokedBy,
            g.RevokedBySystem
                ? SystemActor
                : g.RevokedBy == null
                    ? null
                    : ctx.Users.Where(u => u.Id == g.RevokedBy).Select(u => u.DisplayName).FirstOrDefault(),
            g.SupersededAt,
            g.SupersededByGrantId,
            g.DeniedAt,
            g.DeniedBy,
            g.DeniedBy == null
                ? null
                    : ctx.Users.Where(u => u.Id == g.DeniedBy).Select(u => u.DisplayName).FirstOrDefault(),
            g.LastAccessedAt,
            g.LastAccessIp,
            g.LastAccessHostname,
            // CanRevoke: only an Active grant can be revoked.
            g.Status == GrantStatus.Active,
            // CanGrantAgain: a terminal grant (Expired/Consumed/Denied/Revoked/Superseded) that the agent can be
            // re-granted because it currently has NO active coverage of this entry — no active GRANULAR
            // on the entry and no active FULL on the vault. Coverage is scoped to the same agent. For a
            // GRANULAR grant coverage is checked on its EntryId; for a FULL grant, on an active FULL grant
            // of the same agent in the vault.
            false,
            Array.Empty<Guid>());
    }

    internal static void ApplyAgentSigningIdentity(IList<GrantResponse> grants)
    {
        for (var index = 0; index < grants.Count; index++)
        {
            var encodedKey = grants[index].AgentSigningPublicKey;
            if (string.IsNullOrEmpty(encodedKey))
            {
                continue;
            }

            var rawKey = new byte[32];
            if (!Convert.TryFromBase64String(encodedKey, rawKey, out var written) || written != rawKey.Length)
            {
                continue;
            }

            grants[index] = grants[index] with
            {
                AgentSigningKeyFingerprint = WebEncoders.Base64UrlEncode(
                    VaultKeyFingerprint.Compute(rawKey, VaultKeyKind.AgentEd25519)),
            };
        }
    }

    internal static async Task ApplyEncryptedReasonsAsync(
        VaultDomainReadContext context,
        List<GrantResponse> grants,
        CancellationToken ct)
    {
        var grantIds = grants.Select(grant => grant.Id).ToArray();
        if (grantIds.Length == 0)
        {
            return;
        }

        var encryptedReasons = await context.EncryptedReasonEnvelopes
            .Where(envelope => grantIds.Contains(envelope.GrantRequestId))
            .Select(envelope => new
            {
                envelope.ProtocolVersion, envelope.CryptoSuiteId, envelope.OrganizationId, envelope.VaultId,
                envelope.EntryId, envelope.GrantRequestId, envelope.AgentId, envelope.ResourceRevision,
                envelope.ReasonKeyVersion, envelope.MemberKeyGeneration, envelope.WrapperSuiteId,
                envelope.AgentMessageKeyVersion, envelope.RecipientAgentMessageKeyFingerprint,
                envelope.RequestedMethods, envelope.EncodedSuitePayload, envelope.AgentMessageWrappedReasonDek,
                envelope.AgentSignature,
            })
            .ToDictionaryAsync(envelope => envelope.GrantRequestId, ct);

        for (var index = 0; index < grants.Count; index++)
        {
            if (!encryptedReasons.TryGetValue(grants[index].Id, out var encryptedReason))
            {
                continue;
            }

            var scope = new EnvelopeScopeContract(encryptedReason.OrganizationId, encryptedReason.VaultId,
                encryptedReason.EntryId, encryptedReason.GrantRequestId, encryptedReason.AgentId);
            var binding = new ReasonEnvelopeBindingContract(
                encryptedReason.WrapperSuiteId, encryptedReason.AgentMessageKeyVersion,
                WebEncoders.Base64UrlEncode(encryptedReason.RecipientAgentMessageKeyFingerprint),
                (ushort)encryptedReason.RequestedMethods);
            var descriptor = new EnvelopeDescriptorContract<ReasonEnvelopeBindingContract>(
                encryptedReason.ProtocolVersion, encryptedReason.CryptoSuiteId,
                EnvelopePurposeContract.EncryptedReason, scope,
                encryptedReason.ResourceRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                encryptedReason.ReasonKeyVersion, encryptedReason.MemberKeyGeneration, binding);
            var domainDescriptor = new EnvelopeDescriptor(
                descriptor.ProtocolVersion, new CryptoSuiteId(descriptor.CryptoSuiteId),
                EnvelopePurpose.EncryptedReason,
                new EnvelopeScope(scope.OrganizationId, scope.VaultId, scope.EntryId,
                    scope.GrantOrRequestId, scope.AgentId), encryptedReason.ResourceRevision,
                encryptedReason.ReasonKeyVersion, encryptedReason.MemberKeyGeneration,
                new ReasonEnvelopeBinding(binding.WrapperSuiteId, binding.RecipientKeyVersion,
                    encryptedReason.RecipientAgentMessageKeyFingerprint, binding.RequestedMethods));
            var parentHash = X25519WrapperContextCodec.ComputeParentDescriptorHash(domainDescriptor);
            grants[index] = grants[index] with
            {
                EncryptedReason = new EncryptedReasonEnvelopeContract(
                    descriptor,
                    WebEncoders.Base64UrlEncode(encryptedReason.EncodedSuitePayload),
                    new X25519WrappedKeyContract(
                        new X25519WrapperDescriptorContract(descriptor.ProtocolVersion,
                            binding.WrapperSuiteId, X25519WrapperPurposeContract.ReasonDek, scope,
                            descriptor.ResourceRevision, descriptor.KeyVersion, descriptor.MemberKeyGeneration,
                            X25519RecipientKeyKindContract.VaultMessageX25519,
                            binding.RecipientKeyVersion, binding.RecipientKeyFingerprint,
                            WebEncoders.Base64UrlEncode(parentHash)),
                        WebEncoders.Base64UrlEncode(encryptedReason.AgentMessageWrappedReasonDek)),
                    WebEncoders.Base64UrlEncode(encryptedReason.AgentSignature)),
            };
        }
    }

    internal static async Task ApplyCanGrantAgainAsync(
        VaultDomainReadContext ctx,
        IList<GrantResponse> rows,
        CancellationToken ct)
    {
        var terminalRows = rows.Where(row => row.Status is GrantStatus.Expired
            or GrantStatus.Consumed or GrantStatus.Denied or GrantStatus.Revoked
            or GrantStatus.Superseded).ToArray();
        if (terminalRows.Length == 0)
        {
            return;
        }

        var agentIds = terminalRows.Where(row => row.AgentId is not null).Select(row => row.AgentId!.Value).Distinct().ToArray();
        var vaultIds = terminalRows.Select(row => row.VaultId).Distinct().ToArray();
        var entryIds = terminalRows.Where(row => row.EntryId is not null).Select(row => row.EntryId!.Value).Distinct().ToArray();
        var availableVaultIds = (await ctx.Vaults
                .Where(vault => vaultIds.Contains(vault.Id))
                .Select(vault => vault.Id)
                .ToListAsync(ct))
            .ToHashSet();
        var activeAgentIds = (await ctx.Agents
                .Where(agent => agent.Status == AgentStatus.Active && agentIds.Contains(agent.Id))
                .Select(agent => agent.Id)
                .ToListAsync(ct))
            .ToHashSet();
        var activeFull = (await ctx.Grants.OfType<FullGrant>()
                .Where(grant => grant.Status == GrantStatus.Active
                                && agentIds.Contains(grant.AgentId)
                                && availableVaultIds.Contains(grant.VaultId)
                                && ctx.Agents.Any(agent => agent.Id == grant.AgentId
                                                           && agent.Status == AgentStatus.Active
                                                           && agent.AccessEpoch == grant.AgentAccessEpoch))
                .Select(grant => new { grant.Id, grant.AgentId, grant.VaultId, grant.CreatedAt })
                .ToListAsync(ct))
            .GroupBy(value => (value.AgentId, value.VaultId))
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(value => value.CreatedAt)
                    .ThenByDescending(value => value.Id)
                    .Select(value => value.Id)
                    .ToArray());
        var currentRevisions = (await ctx.Entries
                .Where(entry => entry.State == EntryState.Active
                                && vaultIds.Contains(entry.VaultId)
                                && entryIds.Contains(entry.Id))
                .Select(entry => new { entry.VaultId, EntryId = entry.Id, Revision = entry.CurrentRevision.Value })
                .ToListAsync(ct))
            .ToDictionary(value => (value.VaultId, value.EntryId), value => value.Revision);
        var activeMaterial = await ctx.Grants
            .Where(grant => grant.Status == GrantStatus.Active
                            && agentIds.Contains(grant.AgentId)
                            && availableVaultIds.Contains(grant.VaultId)
                            && ctx.Agents.Any(agent => agent.Id == grant.AgentId
                                                       && agent.Status == AgentStatus.Active
                                                       && agent.AccessEpoch == grant.AgentAccessEpoch))
            .SelectMany(
                grant => grant.GrantEntryScopes.Where(scope =>
                    entryIds.Contains(scope.EntryId) && scope.Envelope != null),
                (grant, scope) => new
                {
                    grant.Id,
                    grant.AgentId,
                    grant.VaultId,
                    grant.CreatedAt,
                    scope.EntryId,
                    Revision = scope.Envelope!.EntryRevision,
                })
            .ToListAsync(ct);

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            if (!terminalRows.Contains(row) || row.AgentId is null)
            {
                continue;
            }

            var isAgentActive = activeAgentIds.Contains(row.AgentId.Value);
            var isVaultAvailable = availableVaultIds.Contains(row.VaultId);
            var isTargetAvailable = isVaultAvailable
                                    && (row.Type == GrantType.Full
                                        || row.EntryId is not null
                                        && currentRevisions.ContainsKey((row.VaultId, row.EntryId.Value)));
            IReadOnlyList<Guid> activeCoveringGrantIds = !isAgentActive || !isTargetAvailable
                ? []
                : row.Type == GrantType.Full
                    ? activeFull.GetValueOrDefault((row.AgentId.Value, row.VaultId), [])
                    : row.EntryId is not null
                      && currentRevisions.TryGetValue((row.VaultId, row.EntryId.Value), out var currentRevision)
                        ? activeMaterial
                            .Where(material => material.AgentId == row.AgentId.Value
                                               && material.VaultId == row.VaultId
                                               && material.EntryId == row.EntryId.Value
                                               && material.Revision == currentRevision)
                            .OrderByDescending(material => material.CreatedAt)
                            .ThenByDescending(material => material.Id)
                            .Select(material => material.Id)
                            .Distinct()
                            .ToArray()
                        : [];
            rows[index] = row with
            {
                CanGrantAgain = isAgentActive
                                && isTargetAvailable
                                && activeCoveringGrantIds.Count == 0,
                ActiveCoveringGrantIds = activeCoveringGrantIds,
            };
        }
    }
}
