using JetBrains.Annotations;

namespace Palladin.Module.Audit.Contracts.ValueObjects;

[PublicAPI]
public enum AuditResult : short
{
    Succeeded = 1,
    Denied = 2,
    Failed = 3,
}
