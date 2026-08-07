namespace Palladin.Module.Audit.Features;

// Multi-select audit filters arrive as CSV in a single query parameter (e.g. agentId=a,b). A single
// value without a comma stays backward compatible. Parsing: split on ',', trim, drop blanks.
internal static class AuditFilterParsing
{
    internal static IReadOnlyList<string> ParseStrings(params string?[] sources) =>
        sources
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .SelectMany(s => s!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct()
            .ToList();

    internal static IReadOnlyList<Guid> ParseGuids(string? csv) =>
        ParseStrings(csv)
            .Select(s => Guid.TryParse(s, out var id) ? id : (Guid?)null)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .ToList();
}
