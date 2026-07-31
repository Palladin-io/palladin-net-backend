using Palladin.Core.Persistence;
using Palladin.Module.Audit.Domain;

namespace Palladin.Module.Audit.Infrastructure.Persistence;

internal sealed class AuditDomainReadContext(AuditDbReadContext readContext) : DomainReadContextBase(readContext)
{
    public IQueryable<AuditLogEntry> AuditLogEntries => Query<AuditLogEntry>();
    public IQueryable<AuditExportJob> AuditExportJobs => Query<AuditExportJob>();
}
