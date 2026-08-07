using Palladin.Core.Events;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Vault.Contracts.Events;

[PublicAPI]
public sealed record VaultDeletedEvent(
    Guid VaultId,
    Guid UserId,
    Guid OrganizationId,
    string ActorName,
    IReadOnlyList<Guid> MemberUserIds,
    Instant UpdatedAt) : IIntegrationEvent
{
    public int MemberCount => MemberUserIds.Count;
}
