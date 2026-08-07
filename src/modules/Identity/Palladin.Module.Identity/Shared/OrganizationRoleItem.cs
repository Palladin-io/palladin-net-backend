using JetBrains.Annotations;

namespace Palladin.Module.Identity.Shared;

[PublicAPI]
public sealed record OrganizationRoleItem(Guid Id, string Name, int Permissions, bool IsSystem);
