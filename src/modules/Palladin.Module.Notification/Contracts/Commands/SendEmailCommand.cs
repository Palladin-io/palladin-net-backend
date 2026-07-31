using Palladin.Core.Events;
using JetBrains.Annotations;
using NodaTime;

namespace Palladin.Module.Notification.Contracts.Commands;

// Cross-module command for the Notification email channel: the owning module resolves the recipient,
// language and template model; Notification renders the template and sends. Template is a name from
// EmailTemplates. Model values are plain strings — the renderer HTML-escapes them, so callers never
// pre-encode.
[PublicAPI]
public sealed record SendEmailCommand(
    string Email,
    string Template,
    string Language,
    IReadOnlyDictionary<string, string> Model,
    Instant OccurredAt,
    string? IdempotencyKey = null) : IIntegrationCommand;
