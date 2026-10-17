using JetBrains.Annotations;
using NodaTime;
using Palladin.Core.Events;

namespace Palladin.Module.Vault.Contracts.Events;

[PublicAPI]
public sealed record EntryShareOtpRequestedEvent(Guid ShareId, Guid SessionId, long Generation, Instant UpdatedAt)
    : IIntegrationEvent;
