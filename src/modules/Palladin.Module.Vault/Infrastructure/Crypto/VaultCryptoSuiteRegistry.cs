using Palladin.Core.Types.Exceptions;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Crypto;

internal interface IVaultEnvelopeSuite
{
    CryptoSuiteId Id { get; }
    void ValidateEncodedPayload(EnvelopePurpose purpose, EncodedSuitePayload payload, int maximumCiphertextBytes);
}

internal interface IVaultCryptoSuiteRegistry
{
    IVaultEnvelopeSuite Resolve(CryptoSuiteId id);
}

internal sealed class VaultCryptoSuiteRegistry(IEnumerable<IVaultEnvelopeSuite> suites)
    : IVaultCryptoSuiteRegistry
{
    private readonly IReadOnlyDictionary<string, IVaultEnvelopeSuite> _suites = suites
        .ToDictionary(x => x.Id.Value, StringComparer.Ordinal);

    public IVaultEnvelopeSuite Resolve(CryptoSuiteId id) =>
        _suites.TryGetValue(id.Value, out var suite)
            ? suite
            : throw new DomainException("The crypto suite is not registered by Vault protocol 2.");
}

internal sealed class XChaCha20Poly1305VaultEnvelopeSuite : IVaultEnvelopeSuite
{
    private const int NonceBytes = 24;
    private const int TagBytes = 16;

    public CryptoSuiteId Id { get; } = new(CryptoSuiteId.XChaCha20Poly1305V1);

    public void ValidateEncodedPayload(
        EnvelopePurpose purpose,
        EncodedSuitePayload payload,
        int maximumCiphertextBytes)
    {
        if (!Enum.IsDefined(purpose) || maximumCiphertextBytes < TagBytes
            || payload.Bytes.Length < NonceBytes + TagBytes
            || payload.Bytes.Length > NonceBytes + maximumCiphertextBytes)
        {
            throw new DomainException("The encoded XChaCha20-Poly1305 payload is outside its purpose limit.");
        }
    }
}

internal static class X25519SealedBoxContract
{
    internal const string SuiteId = "palladin-x25519-sealed-box-v1";
    internal const int EncodedPackageBytes = 120;

    internal static void ValidatePackage(ReadOnlySpan<byte> package)
    {
        if (package.Length != EncodedPackageBytes)
        {
            throw new DomainException("The X25519 sealed-box package must contain exactly 120 bytes.");
        }
    }
}

internal enum X25519WrapperPurpose : ushort
{
    MemberVaultKey = 1,
    AgentDiscoveryVdk = 2,
    ReasonDek = 3,
    GrantDek = 4,
}

internal sealed record X25519WrapperContext(
    X25519WrapperPurpose Purpose,
    EnvelopeScope Scope,
    ulong ResourceRevision,
    uint WrappedKeyVersion,
    uint? MemberKeyGeneration,
    VaultKeyKind RecipientKeyKind,
    uint RecipientKeyVersion,
    byte[] RecipientFingerprint,
    byte[]? ParentDescriptorHash);
