using JetBrains.Annotations;
using NodaTime;
using Palladin.Core.Events;

namespace Palladin.Module.Vault.Contracts.Commands;

// Vault-owned OpenHost command. Identity supplies its authenticated current Member public key and
// monotonic version; Vault materializes only the frozen fingerprint in its local directory.
[PublicAPI]
public sealed record UpsertMemberKeyDirectoryCommand(
    Guid UserId,
    uint KeyVersion,
    byte[] RawPublicKey,
    Instant UpdatedAt) : IIntegrationCommand;
