using Palladin.Core.Events;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Identity.Contracts.Events;

[PublicAPI]
public sealed record WaitlistVerifiedEvent(Guid EntryId, Instant OccurredAt) : IIntegrationEvent;
