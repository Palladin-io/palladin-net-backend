using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Core.Types.Exceptions;
using Palladin.Module.Agents.Infrastructure.AgentAuth;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record CreateAgentPairingActivationRequest(Guid ActivationId);

[PublicAPI]
public sealed record ConfirmAgentPairingActivationRequest(string PairingDigest);

[PublicAPI]
public sealed record AgentPairingActivationResponse(
    Guid ActivationId,
    Guid OrganizationId,
    Guid AgentId,
    uint AgentAccessEpoch,
    string AgentX25519Fingerprint,
    string AgentEd25519Fingerprint,
    Instant ExpiresAt,
    IReadOnlyList<VaultManifestContract> CandidateManifests);

[PublicAPI]
public sealed record AgentPairingStatusResponse(
    Guid ActivationId,
    string Status,
    Instant ExpiresAt,
    string? ConfirmedPairingDigest);

[UsedImplicitly]
internal sealed class CreateAgentPairingActivationValidator
    : Validator<CreateAgentPairingActivationRequest>
{
    public CreateAgentPairingActivationValidator() =>
        RuleFor(x => x.ActivationId).NotEmpty();
}

[UsedImplicitly]
internal sealed class ConfirmAgentPairingActivationValidator
    : Validator<ConfirmAgentPairingActivationRequest>
{
    public ConfirmAgentPairingActivationValidator() =>
        RuleFor(x => x.PairingDigest)
            .Must(AgentPairingEndpointHelpers.IsCanonicalDigest)
            .WithMessage("PairingDigest must be canonical base64url containing exactly 32 bytes.");
}

[PublicAPI]
internal sealed class CreateAgentPairingActivationEndpoint(
    VaultDomainReadContext readContext,
    VaultDomainWriteContext writeContext,
    IClock clock) : Endpoint<CreateAgentPairingActivationRequest, AgentPairingActivationResponse>
{
    private static readonly Duration Lifetime = Duration.FromMinutes(10);

    public override void Configure()
    {
        Post("api/agent/pairing/activations");
        AuthSchemes(AgentAuthenticationOptions.SchemeName);
        Summary(summary =>
        {
            summary.Summary = "Create a bounded Agent first-pairing activation";
            summary.Description = "Returns untrusted candidate manifests. Member and Agent clients independently compute and compare the frozen local transcript digest/SAS.";
        });
        Tags("Vault/Pairing");
    }

    public override async Task HandleAsync(CreateAgentPairingActivationRequest req, CancellationToken ct)
    {
        if (!AgentPairingEndpointHelpers.HasProtocolHeader(HttpContext))
        {
            AddError("unsupported-protocol");
            await Send.ErrorsAsync(426, ct);
            return;
        }

        var agentId = User.GetAgentId();
        var organizationId = User.GetAgentOrganizationId();
        var accessEpoch = User.GetAgentAccessEpoch();
        if (agentId is null || organizationId is null || accessEpoch is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        AgentPairingCandidateSet? candidates;
        try
        {
            candidates = await AgentPairingTranscriptService.LoadAsync(
                readContext,
                organizationId.Value,
                agentId.Value,
                accessEpoch.Value,
                req.ActivationId,
                ct);
        }
        catch (DomainException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(409, ct);
            return;
        }

        if (candidates is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var existing = await writeContext.AgentPairingActivations
            .SingleOrDefaultAsync(x => x.Id == req.ActivationId, ct);
        var now = clock.GetCurrentInstant();
        if (existing is null)
        {
            existing = AgentPairingActivation.Create(
                req.ActivationId,
                organizationId.Value,
                agentId.Value,
                accessEpoch.Value,
                candidates.AgentX25519Fingerprint,
                candidates.AgentEd25519Fingerprint,
                candidates.Digest,
                candidates.Manifests.Count,
                now,
                Lifetime);
            writeContext.Add(existing);
            foreach (var manifest in candidates.Manifests)
            {
                writeContext.Add(AgentPairingActivationCandidate.Create(
                    req.ActivationId,
                    organizationId.Value,
                    agentId.Value,
                    manifest.VaultId,
                    new ManifestRevision(ulong.Parse(
                        manifest.ManifestRevision,
                        System.Globalization.CultureInfo.InvariantCulture)),
                    WebEncoders.Base64UrlDecode(manifest.VaultSigningKeyFingerprint),
                    System.Security.Cryptography.SHA256.HashData(
                        Infrastructure.Crypto.VaultManifestCryptoValidator.CanonicalizeSigned(manifest))));
            }
            await writeContext.CommitAsync(ct);
        }
        else if (existing.OrganizationId != organizationId
                 || existing.AgentId != agentId
                 || existing.AgentAccessEpoch != accessEpoch
                 || !existing.CandidateDigest.AsSpan().SequenceEqual(candidates.Digest))
        {
            AddError("activation-id-conflict");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        await Send.OkAsync(AgentPairingEndpointHelpers.ToResponse(existing, candidates), ct);
    }
}

[PublicAPI]
internal sealed class GetMemberAgentPairingActivationEndpoint(
    VaultDomainReadContext readContext,
    IClock clock) : EndpointWithoutRequest<AgentPairingActivationResponse>
{
    public override void Configure()
    {
        Get("api/agents/{agentId}/pairing/activations/{activationId}");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.AgentManage);
        this.RequireEmailVerified();
        Summary(summary => summary.Summary = "Get untrusted first-pairing candidates for local Member verification");
        Tags("Vault/Pairing");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId();
        var userId = User.GetUserId();
        var agentId = Route<Guid>("agentId");
        var activationId = Route<Guid>("activationId");
        if (organizationId is null || userId is null
            || agentId == Guid.Empty || activationId == Guid.Empty)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var activation = await readContext.AgentPairingActivations
            .SingleOrDefaultAsync(x => x.Id == activationId
                                       && x.OrganizationId == organizationId
                                       && x.AgentId == agentId, ct);
        if (activation is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var candidates = await AgentPairingEndpointHelpers.LoadCurrentCandidatesAsync(
            readContext,
            activation,
            clock.GetCurrentInstant(),
            ct);
        if (candidates is null)
        {
            AddError("pairing-candidate-set-stale");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var vaultIds = candidates.Manifests.Select(x => x.VaultId).ToArray();
        var accessibleCount = await readContext.VaultMembers
            .CountAsync(x => x.OrganizationId == organizationId
                             && x.UserId == userId
                             && vaultIds.Contains(x.VaultId), ct);
        if (accessibleCount != vaultIds.Length)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(AgentPairingEndpointHelpers.ToResponse(activation, candidates), ct);
    }
}

[PublicAPI]
internal sealed class ConfirmAgentPairingActivationEndpoint(
    VaultDomainWriteContext writeContext,
    IClock clock) : Endpoint<ConfirmAgentPairingActivationRequest>
{
    public override void Configure()
    {
        Post("api/agents/{agentId}/pairing/activations/{activationId}/confirm");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.AgentManage);
        this.RequireEmailVerified();
        Summary(summary => summary.Summary = "Confirm an independently verified Agent pairing transcript digest");
        Tags("Vault/Pairing");
    }

    public override async Task HandleAsync(ConfirmAgentPairingActivationRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId();
        var userId = User.GetUserId();
        var agentId = Route<Guid>("agentId");
        var activationId = Route<Guid>("activationId");
        if (organizationId is null || userId is null
            || agentId == Guid.Empty || activationId == Guid.Empty)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        await using var transaction = await writeContext.BeginTransactionAsync(ct);
        // Provisioning and Agent lifecycle mutations take the same transaction-scoped advisory lock.
        // Confirmation therefore validates and persists one coherent candidate set, while the
        // candidate queries remain explicitly no-tracking and bounded.
        await writeContext.LockOrganizationAgentLifecycle(organizationId.Value).SingleAsync(ct);
        var activation = await writeContext.LockAgentPairingActivation(
                organizationId.Value,
                agentId,
                activationId)
            .SingleOrDefaultAsync(ct);
        if (activation is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var now = clock.GetCurrentInstant();
        var candidates = await AgentPairingEndpointHelpers.LoadCurrentCandidatesAsync(
            writeContext,
            activation,
            now,
            ct);
        if (candidates is null)
        {
            AddError("pairing-candidate-set-stale");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var vaultIds = candidates.Manifests.Select(x => x.VaultId).ToArray();
        var accessibleCount = await writeContext.VaultMembers
            .AsNoTracking()
            .CountAsync(x => x.OrganizationId == organizationId
                             && x.UserId == userId
                             && vaultIds.Contains(x.VaultId), ct);
        if (accessibleCount != vaultIds.Length)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        try
        {
            activation.Confirm(
                userId.Value,
                WebEncoders.Base64UrlDecode(req.PairingDigest),
                now);
            await writeContext.CommitAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DomainException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(409, ct);
            return;
        }

        await Send.NoContentAsync(ct);
    }
}

[PublicAPI]
internal sealed class GetAgentPairingStatusEndpoint(
    VaultDomainReadContext readContext,
    IClock clock) : EndpointWithoutRequest<AgentPairingStatusResponse>
{
    public override void Configure()
    {
        Get("api/agent/pairing/activations/{activationId}");
        AuthSchemes(AgentAuthenticationOptions.SchemeName);
        Summary(summary => summary.Summary = "Poll Member confirmation of an Agent pairing digest");
        Tags("Vault/Pairing");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var agentId = User.GetAgentId();
        var organizationId = User.GetAgentOrganizationId();
        var accessEpoch = User.GetAgentAccessEpoch();
        var activationId = Route<Guid>("activationId");
        if (agentId is null || organizationId is null || accessEpoch is null
            || activationId == Guid.Empty)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var activation = await readContext.AgentPairingActivations
            .SingleOrDefaultAsync(x => x.Id == activationId
                                       && x.OrganizationId == organizationId
                                       && x.AgentId == agentId
                                       && x.AgentAccessEpoch == accessEpoch, ct);
        if (activation is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var now = clock.GetCurrentInstant();
        var candidates = await AgentPairingEndpointHelpers.LoadCurrentCandidatesAsync(
            readContext,
            activation,
            now,
            ct);
        var status = activation.ConfirmedAt is null && now >= activation.ExpiresAt
            ? "expired"
            : candidates is null
                ? "stale"
                : activation.ConfirmedAt is not null ? "confirmed" : "pending";
        var digest = status == "confirmed"
            ? WebEncoders.Base64UrlEncode(activation.CandidateDigest)
            : null;
        await Send.OkAsync(new AgentPairingStatusResponse(
            activation.Id,
            status,
            activation.ExpiresAt,
            digest), ct);
    }
}

internal static class AgentPairingEndpointHelpers
{
    private const string ProtocolHeader = "X-Palladin-Vault-Protocol";

    internal static bool HasProtocolHeader(HttpContext context) =>
        context.Request.Headers[ProtocolHeader].ToString() == VaultProtocol.CurrentVersion.ToString();

    internal static bool IsCanonicalDigest(string value)
    {
        try
        {
            var decoded = WebEncoders.Base64UrlDecode(value);
            return decoded.Length == 32 && WebEncoders.Base64UrlEncode(decoded) == value;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    internal static async Task<AgentPairingCandidateSet?> LoadCurrentCandidatesAsync(
        VaultDomainReadContext readContext,
        AgentPairingActivation activation,
        Instant now,
        CancellationToken ct)
    {
        if (activation.ConfirmedAt is null && now >= activation.ExpiresAt)
        {
            return null;
        }

        AgentPairingCandidateSet? candidates;
        try
        {
            candidates = await AgentPairingTranscriptService.LoadAsync(
                readContext,
                activation.OrganizationId,
                activation.AgentId,
                activation.AgentAccessEpoch,
                activation.Id,
                ct);
        }
        catch (DomainException)
        {
            return null;
        }

        return candidates is not null
               && activation.CandidateDigest.AsSpan().SequenceEqual(candidates.Digest)
               && activation.CandidateVaultCount == candidates.Manifests.Count
            ? candidates
            : null;
    }

    internal static async Task<AgentPairingCandidateSet?> LoadCurrentCandidatesAsync(
        VaultDomainWriteContext writeContext,
        AgentPairingActivation activation,
        Instant now,
        CancellationToken ct)
    {
        if (activation.ConfirmedAt is null && now >= activation.ExpiresAt)
        {
            return null;
        }

        AgentPairingCandidateSet? candidates;
        try
        {
            candidates = await AgentPairingTranscriptService.LoadAsync(
                writeContext,
                activation.OrganizationId,
                activation.AgentId,
                activation.AgentAccessEpoch,
                activation.Id,
                ct);
        }
        catch (DomainException)
        {
            return null;
        }

        return candidates is not null
               && activation.CandidateDigest.AsSpan().SequenceEqual(candidates.Digest)
               && activation.CandidateVaultCount == candidates.Manifests.Count
            ? candidates
            : null;
    }

    internal static AgentPairingActivationResponse ToResponse(
        AgentPairingActivation activation,
        AgentPairingCandidateSet candidates) => new(
        activation.Id,
        activation.OrganizationId,
        activation.AgentId,
        activation.AgentAccessEpoch,
        WebEncoders.Base64UrlEncode(activation.AgentX25519Fingerprint),
        WebEncoders.Base64UrlEncode(activation.AgentEd25519Fingerprint),
        activation.ExpiresAt,
        candidates.Manifests);
}
