namespace Palladin.Core.Security;

[Flags]
public enum Permission
{
    None = 0,
    AddUser = 1,
    OrganizationManagement = 2,
    VaultCreate = 4,
    VaultManage = 8,
    AgentManage = 16,
    GrantManage = 32,
    BillingManage = 64,
    AuditView = 128,
    // 256 and 512 are reserved for billing plan feature flags
    // (PERMISSION_MULTIPLE_VAULTS, PERMISSION_FULL_GRANT_MODE in the web panel).
    ReadApiKey = 4096,
    WriteApiKey = 8192,
}
