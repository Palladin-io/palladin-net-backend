using Palladin.Core.Events;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Identity.Contracts.Events;

// Email, language and the approximate client exist only for the account-recovered security e-mail;
// analytics and audit receive the identifiers alone.
[PublicAPI]
public sealed record AccountRecoveryCompletedEvent(
    Guid UserId,
    Guid OrganizationId,
    string DisplayName,
    string Email,
    string PreferredLanguage,
    string Client,
    Instant UpdatedAt) : IIntegrationEvent;
