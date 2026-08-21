using JetBrains.Annotations;
using NodaTime;
using Palladin.Core.Events;

namespace Palladin.Module.Vault.Contracts.Events;

[PublicAPI]
public sealed record RoleVaultAccessPolicyChangedEvent(
    Guid OrganizationId,
    Guid RoleId,
    Guid OperationId,
    ulong Revision,
    Guid ChangedBy,
    IReadOnlyList<Guid> SelectedVaultIds,
    Instant OccurredAt) : IIntegrationEvent;
