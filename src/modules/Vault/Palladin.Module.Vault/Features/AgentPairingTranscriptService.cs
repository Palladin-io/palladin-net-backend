using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Palladin.Core.Types;
using Palladin.Core.Types.Exceptions;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Features;

internal sealed record AgentPairingCandidateSet(
    Guid ActivationId,
    Guid OrganizationId,
    Guid AgentId,
    uint AgentAccessEpoch,
    byte[] AgentX25519Fingerprint,
    byte[] AgentEd25519Fingerprint,
    byte[] Digest,
    IReadOnlyList<VaultManifestContract> Manifests);

internal static class AgentPairingTranscriptService
{
    internal const int MaximumCandidateVaults = 2048;
    private static readonly byte[] DomainPrefix = Encoding.ASCII.GetBytes("PLDNV2PAIR:TRANSCRIPT:");

    internal static async Task<AgentPairingCandidateSet?> LoadAsync(
        VaultDomainReadContext readContext,
        Guid organizationId,
        Guid agentId,
        uint accessEpoch,
        Guid activationId,
        CancellationToken ct) => await LoadAsync(
        readContext.Agents,
        readContext.Vaults,
        readContext.AgentVaultDiscoveryEnvelopes,
        organizationId,
        agentId,
        accessEpoch,
        activationId,
        ct);

    internal static async Task<AgentPairingCandidateSet?> LoadAsync(
        VaultDomainWriteContext writeContext,
        Guid organizationId,
        Guid agentId,
        uint accessEpoch,
        Guid activationId,
        CancellationToken ct) => await LoadAsync(
        writeContext.Agents,
        writeContext.Vaults,
        writeContext.AgentVaultDiscoveryEnvelopes,
        organizationId,
        agentId,
        accessEpoch,
        activationId,
        ct);

    private static async Task<AgentPairingCandidateSet?> LoadAsync(
        IQueryable<Agent> agents,
        IQueryable<Domain.Vault> vaults,
        IQueryable<AgentVaultDiscoveryEnvelope> discoveryEnvelopes,
        Guid organizationId,
        Guid agentId,
        uint accessEpoch,
        Guid activationId,
        CancellationToken ct)
    {
        var agent = await agents.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId
                        && x.Id == agentId
                        && x.Status == AgentStatus.Active
                        && x.AccessEpoch == accessEpoch)
            .Select(x => new { x.PublicKey, x.SigningPublicKey })
            .SingleOrDefaultAsync(ct);
        if (agent is null)
        {
            return null;
        }

        var vaultCount = await vaults.AsNoTracking()
            .CountAsync(x => x.OrganizationId == organizationId, ct);
        if (vaultCount > MaximumCandidateVaults)
        {
            throw new DomainException("Agent pairing candidate Vault limit was exceeded.");
        }

        // DomainReadContext is no-tracking. Take one sentinel row so neither EF tracking nor
        // response memory can silently grow beyond the protocol's bounded pairing ceremony.
        var envelopes = await discoveryEnvelopes.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId
                        && x.AgentId == agentId
                        && x.RevokedAt == null
                        && x.ProvisionedAccessEpoch == accessEpoch)
            .Where(x => vaults.Any(vault =>
                vault.OrganizationId == x.OrganizationId
                && vault.Id == x.VaultId
                && vault.CurrentVdkVersion == x.VdkVersion
                && vault.CurrentManifestSigningKeyVersion == x.ManifestSigningKeyVersion
                && vault.CurrentAgentMessageKeyVersion == x.AgentMessageKeyVersion))
            .OrderBy(x => x.VaultId)
            .Take(MaximumCandidateVaults + 1)
            .ToListAsync(ct);
        if (envelopes.Count > MaximumCandidateVaults)
        {
            throw new DomainException("Agent pairing candidate Vault limit was exceeded.");
        }

        if (envelopes.Count != vaultCount)
        {
            throw new DomainException("Agent pairing is not ready until every Vault has current Discovery provisioning.");
        }

        byte[] x25519Key;
        byte[] ed25519Key;
        try
        {
            x25519Key = Convert.FromBase64String(agent.PublicKey);
            ed25519Key = Convert.FromBase64String(agent.SigningPublicKey);
        }
        catch (FormatException)
        {
            throw new DomainException("Agent pairing identity keys are malformed.");
        }

        var x25519Fingerprint = VaultKeyFingerprint.Compute(x25519Key, VaultKeyKind.AgentX25519);
        var ed25519Fingerprint = VaultKeyFingerprint.Compute(ed25519Key, VaultKeyKind.AgentEd25519);
        var manifests = envelopes.Select(VaultEnvelopeContractMapper.ToManifestContract).ToList();
        var digest = ComputeDigest(
            activationId,
            organizationId,
            agentId,
            x25519Fingerprint,
            ed25519Fingerprint,
            manifests);

        return new AgentPairingCandidateSet(
            activationId,
            organizationId,
            agentId,
            accessEpoch,
            x25519Fingerprint,
            ed25519Fingerprint,
            digest,
            manifests);
    }

    internal static byte[] ComputeDigest(
        Guid activationId,
        Guid organizationId,
        Guid agentId,
        byte[] agentX25519Fingerprint,
        byte[] agentEd25519Fingerprint,
        IReadOnlyList<VaultManifestContract> manifests)
    {
        var canonical = Canonicalize(
            activationId,
            organizationId,
            agentId,
            agentX25519Fingerprint,
            agentEd25519Fingerprint,
            manifests);
        var input = new byte[DomainPrefix.Length + sizeof(ushort) + canonical.Length];
        DomainPrefix.CopyTo(input, 0);
        BinaryPrimitives.WriteUInt16BigEndian(input.AsSpan(DomainPrefix.Length), VaultProtocol.CurrentVersion);
        canonical.CopyTo(input, DomainPrefix.Length + sizeof(ushort));
        return SHA256.HashData(input);
    }

    private static byte[] Canonicalize(
        Guid activationId,
        Guid organizationId,
        Guid agentId,
        byte[] agentX25519Fingerprint,
        byte[] agentEd25519Fingerprint,
        IReadOnlyList<VaultManifestContract> manifests)
    {
        var output = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = false,
        });
        writer.WriteStartObject();
        writer.WriteString("activationId", activationId.ToString("D"));
        writer.WriteString("agentEd25519Fingerprint", WebEncoders.Base64UrlEncode(agentEd25519Fingerprint));
        writer.WriteString("agentId", agentId.ToString("D"));
        writer.WriteString("agentX25519Fingerprint", WebEncoders.Base64UrlEncode(agentX25519Fingerprint));
        writer.WriteString("cryptoSuiteId", CryptoSuiteId.XChaCha20Poly1305V1);
        writer.WriteString("organizationId", organizationId.ToString("D"));
        writer.WriteNumber("protocolVersion", VaultProtocol.CurrentVersion);
        writer.WriteStartArray("vaults");
        foreach (var manifest in manifests)
        {
            writer.WriteStartObject();
            writer.WriteString("manifestRevision", manifest.ManifestRevision);
            writer.WriteString(
                "signedManifestDigest",
                WebEncoders.Base64UrlEncode(SHA256.HashData(
                    VaultManifestCryptoValidator.CanonicalizeSigned(manifest))));
            writer.WriteString("vaultId", manifest.VaultId.ToString("D"));
            writer.WriteString("vaultSigningKeyFingerprint", manifest.VaultSigningKeyFingerprint);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return output.WrittenSpan.ToArray();
    }
}
