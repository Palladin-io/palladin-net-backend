using Palladin.Core.Events;
using JetBrains.Annotations;

namespace Palladin.Module.Identity.Contracts.Events;

[PublicAPI]
public sealed record UserLoggedOutEvent(Guid UserId) : IIntegrationEvent;
