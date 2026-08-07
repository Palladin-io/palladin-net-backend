using Palladin.Core.Events;
using Palladin.Core.Persistence;
using Palladin.Module.Audit.Domain;

namespace Palladin.Module.Audit.Infrastructure.Persistence;

internal sealed class AuditDomainWriteContext(
    AuditDbWriteContext writeContext,
    IEnumerable<IEventPublisher> eventPublishers)
    : DomainWriteContextBase(writeContext, eventPublishers)
{
    public IQueryable<AuditLogEntry> AuditLogEntries => Track<AuditLogEntry>();
    public IQueryable<AuditExportJob> AuditExportJobs => Track<AuditExportJob>();
}
