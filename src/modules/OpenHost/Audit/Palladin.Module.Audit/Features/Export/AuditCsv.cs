namespace Palladin.Module.Audit.Features.Export;

// Minimal RFC-4180 CSV field escaping. Avoids a third-party dependency for the small, fixed schema of
// the audit export. Values containing a comma, quote or newline are wrapped in quotes; inner quotes
// are doubled. Rows are written straight to a TextWriter so the full CSV is never materialized in memory.
internal static class AuditCsv
{
    internal static readonly string[] Header =
    [
        "occurredAt", "eventType", "actorType", "result", "actorName", "agentName",
        "userId", "agentId", "vaultId", "entryId", "metadata",
    ];

    internal static async Task WriteRowAsync(TextWriter writer, IReadOnlyList<string?> fields)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0)
            {
                await writer.WriteAsync(',');
            }

            await writer.WriteAsync(Escape(fields[i]));
        }

        await writer.WriteAsync('\n');
    }

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }
}
