using Palladin.Core.Events;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Identity.Contracts.Events;

[PublicAPI]
public sealed record TotpEnabledEvent(
    Guid UserId,
    Instant EnabledAt) : IIntegrationEvent;
