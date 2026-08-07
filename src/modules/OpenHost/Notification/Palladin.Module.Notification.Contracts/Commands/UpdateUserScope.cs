using Palladin.Core.Events;
using Palladin.Core.Types;
using JetBrains.Annotations;

namespace Palladin.Module.Notification.Contracts.Commands;

[PublicAPI]
public sealed record UpdateUserScope(
    Guid OrganizationId,
    Guid UserId,
    string Type,
    Guid ItemId,
    ScopeAction Action) : IIntegrationCommand;
