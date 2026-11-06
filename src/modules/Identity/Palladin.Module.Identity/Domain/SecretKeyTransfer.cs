using System.Security.Cryptography;
using NodaTime;
using Palladin.Module.Identity.Infrastructure.Crypto;

namespace Palladin.Module.Identity.Domain;

internal enum SecretKeyTransferFlow
{
    RecipientShowsQr = 1,
    SenderShowsQr = 2,
}

internal enum SecretKeyTransferState
{
    WaitingForRecipient = 1,
    WaitingForSender = 2,
    Bound = 3,
    Sealed = 4,
    Claimed = 5,
}

// Ciphertext-only relay for identity-kdf v3 devicePairing. The backend stores the recipient's public key, the
// SHA-256 of the poll token and, briefly, the sealed Account Secret; it never sees the Account Secret or a SAS.
internal sealed class SecretKeyTransfer
{
    internal const int ChallengeBytes = 32;
    internal const int PollTokenBytes = 32;
    internal const int SealedSecretBytes = 48;
    internal static readonly Duration Lifetime = Duration.FromSeconds(300);

    public Guid Id { get; private set; }
    public SecretKeyTransferFlow Flow { get; private set; }
    public SecretKeyTransferState State { get; private set; }
    public byte[] Challenge { get; private set; } = [];
    public byte[]? PollTokenHash { get; private set; }
    public byte[]? RecipientPublicKey { get; private set; }
    public Guid? SenderUserId { get; private set; }
    public byte[]? KemCiphertext { get; private set; }
    public byte[]? SealedSecret { get; private set; }
    public Instant CreatedAt { get; private set; }
    public Instant ExpiresAt { get; private set; }
    public uint Revision { get; private set; }

    private SecretKeyTransfer() { }

    internal static (SecretKeyTransfer Transfer, byte[] PollToken) StartByRecipient(
        byte[] recipientPublicKey,
        Instant now)
    {
        ValidateRecipientKey(recipientPublicKey);
        var pollToken = RandomNumberGenerator.GetBytes(PollTokenBytes);
        var transfer = New(SecretKeyTransferFlow.RecipientShowsQr, SecretKeyTransferState.WaitingForSender, now);
        transfer.RecipientPublicKey = recipientPublicKey.ToArray();
        transfer.PollTokenHash = SHA256.HashData(pollToken);
        return (transfer, pollToken);
    }

    internal static SecretKeyTransfer StartBySender(Guid senderUserId, Instant now)
    {
        var transfer = New(SecretKeyTransferFlow.SenderShowsQr, SecretKeyTransferState.WaitingForRecipient, now);
        transfer.SenderUserId = senderUserId;
        return transfer;
    }

    internal bool IsExpired(Instant now) => now >= ExpiresAt;

    internal bool MatchesChallenge(byte[] challenge) =>
        challenge.Length == ChallengeBytes && CryptographicOperations.FixedTimeEquals(challenge, Challenge);

    internal bool MatchesPollToken(byte[] pollToken) =>
        PollTokenHash is not null && pollToken.Length == PollTokenBytes
        && CryptographicOperations.FixedTimeEquals(SHA256.HashData(pollToken), PollTokenHash);

    internal bool IsVisibleTo(Guid userId) => SenderUserId is null || SenderUserId == userId;

    internal byte[] AttachRecipient(byte[] recipientPublicKey)
    {
        if (State != SecretKeyTransferState.WaitingForRecipient)
        {
            throw new SecretKeyTransferStateException("pairing-consumed");
        }

        ValidateRecipientKey(recipientPublicKey);
        var pollToken = RandomNumberGenerator.GetBytes(PollTokenBytes);
        RecipientPublicKey = recipientPublicKey.ToArray();
        PollTokenHash = SHA256.HashData(pollToken);
        Advance(SecretKeyTransferState.Bound);
        return pollToken;
    }

    internal void BindSender(Guid senderUserId)
    {
        if (State == SecretKeyTransferState.WaitingForSender)
        {
            SenderUserId = senderUserId;
            Advance(SecretKeyTransferState.Bound);
        }
    }

    internal void Seal(Guid senderUserId, byte[] kemCiphertext, byte[] sealedSecret)
    {
        if (SenderUserId != senderUserId || State != SecretKeyTransferState.Bound)
        {
            throw new SecretKeyTransferStateException("pairing-consumed");
        }

        if (kemCiphertext.Length != DevicePairingProtocol.KemCiphertextBytes || sealedSecret.Length != SealedSecretBytes)
        {
            throw new SecretKeyTransferStateException("malformed-envelope");
        }

        KemCiphertext = kemCiphertext.ToArray();
        SealedSecret = sealedSecret.ToArray();
        Advance(SecretKeyTransferState.Sealed);
    }

    internal (Guid AccountId, byte[] KemCiphertext, byte[] SealedSecret)? Claim()
    {
        if (State != SecretKeyTransferState.Sealed)
        {
            return null;
        }

        var envelope = (SenderUserId!.Value, KemCiphertext!, SealedSecret!);
        KemCiphertext = null;
        SealedSecret = null;
        Advance(SecretKeyTransferState.Claimed);
        return envelope;
    }

    private static SecretKeyTransfer New(
        SecretKeyTransferFlow flow,
        SecretKeyTransferState state,
        Instant now) => new()
    {
        Id = new Guid(RandomNumberGenerator.GetBytes(16)),
        Flow = flow,
        State = state,
        Challenge = RandomNumberGenerator.GetBytes(ChallengeBytes),
        CreatedAt = now,
        ExpiresAt = now + Lifetime,
        Revision = 1,
    };

    private static void ValidateRecipientKey(byte[] recipientPublicKey)
    {
        if (recipientPublicKey.Length != DevicePairingProtocol.RecipientPublicKeyBytes)
        {
            throw new SecretKeyTransferStateException("malformed-recipient-key");
        }
    }

    private void Advance(SecretKeyTransferState state)
    {
        State = state;
        Revision++;
    }
}

internal sealed class SecretKeyTransferStateException(string errorKey) : Exception(errorKey)
{
    internal string ErrorKey { get; } = errorKey;
}
