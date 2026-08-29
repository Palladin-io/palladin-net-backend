namespace Palladin.Core.Security;

public static class JwtClaimNames
{
    public const string OrganizationId = "org_id";
    public const string Permissions = "permissions";
    public const string AuthorizationVersion = "authz_ver";
    public const string OrganizationOfflineAccessPolicy = "org_offline_policy";
    public const string OrganizationOfflineAccessPolicyVersion = "org_offline_policy_ver";
    public const string Plan = "plan";
    public const string DisplayName = "display_name";
    public const string EmailVerified = "email_verified";
}
