using JetBrains.Annotations;
using NodaTime;
using Palladin.Core.Events;

namespace Palladin.Module.Vault.Contracts.Events;

[PublicAPI]
public sealed record VaultMemberAccessRemovedEvent(
    Guid OrganizationId,
    Guid VaultId,
    Guid UserId,
    ulong MemberSequence,
    ulong MutationVersion,
    Instant OccurredAt) : IIntegrationEvent;
