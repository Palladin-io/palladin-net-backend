using System.Text.Json;
using Palladin.Module.Notification.Domain;

namespace Palladin.Module.Notification.Infrastructure.Email.Events;

// Parses an SES event-publishing payload (delivered raw from SNS->SQS). Only permanent bounces and
// complaints produce suppressions; transient/undetermined bounces, deliveries and rejects are
// classified for logging but never suppress a recipient.
internal static class SesEventParser
{
    public static SesEventParseResult Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        // An SNS envelope (Type + Message) means RawMessageDelivery is disabled on the subscription —
        // unwrap the inner SES event instead of silently classifying the envelope as Unknown.
        if (ReadString(root, "Type") is "Notification" && ReadString(root, "Message") is { } innerJson)
        {
            return Parse(innerJson) with { Enveloped = true };
        }

        var eventType = ReadString(root, "eventType") ?? ReadString(root, "notificationType") ?? "Unknown";
        var messageId = root.TryGetProperty("mail", out var mail) ? ReadString(mail, "messageId") : null;

        return eventType.ToLowerInvariant() switch
        {
            "bounce" => ParseBounce(root, messageId),
            "complaint" => ParseComplaint(root, messageId),
            _ => new SesEventParseResult(eventType, [], messageId),
        };
    }

    private static SesEventParseResult ParseBounce(JsonElement root, string? messageId)
    {
        if (!root.TryGetProperty("bounce", out var bounce))
        {
            return new SesEventParseResult("Bounce", [], messageId);
        }

        var bounceType = ReadString(bounce, "bounceType") ?? "Undetermined";
        var kind = $"Bounce/{bounceType}";

        // Only a permanent (hard) bounce suppresses — a transient bounce may still succeed later.
        if (!string.Equals(bounceType, "Permanent", StringComparison.OrdinalIgnoreCase))
        {
            return new SesEventParseResult(kind, [], messageId);
        }

        var suppressions = ReadRecipients(bounce, "bouncedRecipients")
            .Select(address => new SesSuppression(address, SuppressionReason.HardBounce))
            .ToList();

        return new SesEventParseResult(kind, suppressions, messageId);
    }

    private static SesEventParseResult ParseComplaint(JsonElement root, string? messageId)
    {
        if (!root.TryGetProperty("complaint", out var complaint))
        {
            return new SesEventParseResult("Complaint", [], messageId);
        }

        var suppressions = ReadRecipients(complaint, "complainedRecipients")
            .Select(address => new SesSuppression(address, SuppressionReason.Complaint))
            .ToList();

        return new SesEventParseResult("Complaint", suppressions, messageId);
    }

    private static IEnumerable<string> ReadRecipients(JsonElement parent, string arrayProperty)
    {
        if (!parent.TryGetProperty(arrayProperty, out var recipients) || recipients.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return recipients
            .EnumerateArray()
            .Select(recipient => ReadString(recipient, "emailAddress"))
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .Select(address => address!)
            .ToList();
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
