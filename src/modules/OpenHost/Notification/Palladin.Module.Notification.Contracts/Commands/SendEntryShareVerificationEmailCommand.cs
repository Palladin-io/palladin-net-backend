using JetBrains.Annotations;
using NodaTime;
using Palladin.Core.Events;

namespace Palladin.Module.Notification.Contracts.Commands;

[PublicAPI]
public sealed record SendEntryShareVerificationEmailCommand(
    Guid ShareId, Guid SessionId, long Generation, string Email, string Code, string Language,
    Instant ExpiresAt, Instant OccurredAt) : IIntegrationCommand
{
    public override string ToString() => nameof(SendEntryShareVerificationEmailCommand);
}
