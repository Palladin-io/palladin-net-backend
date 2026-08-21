namespace Palladin.Core.Security;

[AttributeUsage(AttributeTargets.Class)]
public sealed class RequireAssignableOrganizationRoleAttribute : Attribute
{
    public const string AuthorizedRevisionItemName = "palladin.organization-role-authorized-revision";
    public const string ActorPermissionsItemName = "palladin.organization-role-actor-permissions";
    public const string ActorIsOwnerItemName = "palladin.organization-role-actor-is-owner";
}
