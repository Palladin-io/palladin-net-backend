using JetBrains.Annotations;
using NodaTime;
using Palladin.Core.Events;

namespace Palladin.Module.Notification.Contracts.Commands;

[PublicAPI]
public sealed record DisconnectOrganizationMemberCommand(
    Guid OrganizationId,
    Guid UserId,
    uint AuthorizationVersion,
    Instant OccurredAt) : IIntegrationCommand;
