using JetBrains.Annotations;

namespace Palladin.Module.Audit.Contracts.ValueObjects;

[PublicAPI]
public enum AuditActorType
{
    User = 1,
    Agent = 2,
    System = 3,
}
