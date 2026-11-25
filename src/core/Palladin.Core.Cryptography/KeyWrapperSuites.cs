using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using JetBrains.Annotations;
using Palladin.Core.Types.Exceptions;

namespace Palladin.Core.Cryptography;

[PublicAPI]
public enum KeyWrapperSuiteStatus
{
    Active = 1,
    ReadOnly = 2,
    Retired = 3,
}

// Build-time registry mirroring palladin-protocol contracts/key-wrapper/v1/registry.json. There is no runtime
// registration or negotiation: the suite expected for a recipient always comes from its authenticated key directory.
[PublicAPI]
public sealed class KeyWrapperSuite
{
    private const ushort ProtocolVersion = 2;
    private static readonly byte[] FingerprintDomain = "PLDNV2FP"u8.ToArray();

    private readonly bool _suiteBoundFingerprint;

    private KeyWrapperSuite(
        string id,
        KeyWrapperSuiteStatus status,
        int publicKeyBytes,
        int packageBytes,
        bool suiteBoundFingerprint)
    {
        Id = id;
        Status = status;
        PublicKeyBytes = publicKeyBytes;
        PackageBytes = packageBytes;
        _suiteBoundFingerprint = suiteBoundFingerprint;
    }

    public string Id { get; }
    public KeyWrapperSuiteStatus Status { get; }
    public int PublicKeyBytes { get; }
    public int PackageBytes { get; }
    public bool CanSeal => Status == KeyWrapperSuiteStatus.Active;
    public bool CanOpen => Status is KeyWrapperSuiteStatus.Active or KeyWrapperSuiteStatus.ReadOnly;

    internal static KeyWrapperSuite XWing { get; } =
        new("palladin-xwing-v1", KeyWrapperSuiteStatus.Active, XWingKem.PublicKeyBytes, 1168, true);

    // Frozen libsodium crypto_box_seal suite; kept readable only while existing envelopes are re-wrapped.
    internal static KeyWrapperSuite X25519SealedBox { get; } =
        new("palladin-x25519-sealed-box-v1", KeyWrapperSuiteStatus.ReadOnly, 32, 120, false);

    public void ValidatePublicKey(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != PublicKeyBytes)
        {
            throw new DomainException("The recipient public key length does not match its key-wrapper suite.");
        }
    }

    public void ValidatePackage(ReadOnlySpan<byte> package)
    {
        if (package.Length != PackageBytes)
        {
            throw new DomainException("The sealed key package length does not match its key-wrapper suite.");
        }
    }

    public byte[] Fingerprint(ushort recipientKeyKind, ReadOnlySpan<byte> publicKey)
    {
        ValidatePublicKey(publicKey);
        var suite = _suiteBoundFingerprint ? Encoding.ASCII.GetBytes(Id) : [];
        var preimage = new byte[FingerprintDomain.Length + 4 + (_suiteBoundFingerprint ? 2 + suite.Length : 0)
            + publicKey.Length];
        var span = preimage.AsSpan();
        FingerprintDomain.CopyTo(span);
        var offset = FingerprintDomain.Length;
        BinaryPrimitives.WriteUInt16BigEndian(span[offset..], ProtocolVersion);
        BinaryPrimitives.WriteUInt16BigEndian(span[(offset + 2)..], recipientKeyKind);
        offset += 4;
        if (_suiteBoundFingerprint)
        {
            BinaryPrimitives.WriteUInt16BigEndian(span[offset..], checked((ushort)suite.Length));
            suite.CopyTo(span[(offset + 2)..]);
            offset += 2 + suite.Length;
        }

        publicKey.CopyTo(span[offset..]);
        return SHA256.HashData(preimage);
    }
}

[PublicAPI]
public static class KeyWrapperSuites
{
    private static readonly KeyWrapperSuite[] Registered = [KeyWrapperSuite.XWing, KeyWrapperSuite.X25519SealedBox];

    public static KeyWrapperSuite Current => KeyWrapperSuite.XWing;

    public static int MaximumPublicKeyBytes { get; } = Registered.Max(suite => suite.PublicKeyBytes);

    public static int MaximumPackageBytes { get; } = Registered.Max(suite => suite.PackageBytes);

    public static KeyWrapperSuite Resolve(string? suiteId) =>
        Registered.FirstOrDefault(suite => string.Equals(suite.Id, suiteId, StringComparison.Ordinal))
        ?? throw new DomainException("The key-wrapper suite is not registered.");

    public static KeyWrapperSuite ResolveForOpen(string? suiteId)
    {
        var suite = Resolve(suiteId);
        return suite.CanOpen
            ? suite
            : throw new DomainException("The key-wrapper suite is retired.");
    }

    public static KeyWrapperSuite ResolveForSeal(string? suiteId)
    {
        var suite = Resolve(suiteId);
        return suite.CanSeal
            ? suite
            : throw new DomainException("The key-wrapper suite no longer accepts new envelopes.");
    }
}
