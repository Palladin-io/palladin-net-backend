using Palladin.Core.Events;
using JetBrains.Annotations;

namespace Palladin.Module.Identity.Contracts.Events;

[PublicAPI]
public sealed record UserLoggedInEvent(Guid UserId, string Provider, string Platform, bool IsNewUser) : IIntegrationEvent;
