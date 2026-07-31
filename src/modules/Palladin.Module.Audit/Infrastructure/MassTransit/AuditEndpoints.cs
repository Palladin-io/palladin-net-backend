namespace Palladin.Module.Audit.Infrastructure.MassTransit;

internal static class AuditEndpoints
{
    internal const string FromVault = "audit.events.vault";
    internal const string FromAgents = "audit.events.agents";
    internal const string FromIdentity = "audit.events.identity";
    internal const string Self = "audit.events.self";
    internal const string Append = "audit.commands.append";

    // Audit-trail consumer for export requests lives on its own queue so a retry of the audit write
    // never re-triggers the analytics consumer (which shares the same event on Self).
    internal const string SelfExport = "audit.events.export";
}
