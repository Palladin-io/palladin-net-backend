using JetBrains.Annotations;
using NodaTime;
using Palladin.Core.Events;

namespace Palladin.Module.Vault.Contracts.Commands;

// Vault-owned OpenHost command. Identity supplies its authenticated current Member public key, the key-wrapper
// suite it is registered for and its monotonic version; Vault derives the suite-bound fingerprint locally.
[PublicAPI]
public sealed record UpsertMemberKeyDirectoryCommand(
    Guid UserId,
    uint KeyVersion,
    string PublicKeySuiteId,
    byte[] RawPublicKey,
    Instant UpdatedAt) : IIntegrationCommand;
