namespace Palladin.Module.Notification.Shared;

internal sealed record VaultSyncInvalidationPayload(
    int ProtocolVersion,
    Guid VaultId,
    string MemberSequence,
    string MutationVersion,
    bool Removed);
