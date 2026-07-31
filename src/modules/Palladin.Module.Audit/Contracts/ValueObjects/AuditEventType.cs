using JetBrains.Annotations;

namespace Palladin.Module.Audit.Contracts.ValueObjects;

// Audit event type identifiers (dot-notation). Part of Audit's OpenHost published language — owning
// modules set these on the AppendAuditLogCommand they publish. Values are a stable wire/filter
// contract (the panel filters by them); never change an existing string.
[PublicAPI]
public static class AuditEventType
{
    public const string GrantCreated = "grant.created";
    public const string GrantRequested = "grant.requested";
    public const string GrantApproved = "grant.approved";
    public const string GrantDenied = "grant.denied";
    public const string GrantRevoked = "grant.revoked";
    public const string GrantConsumed = "grant.consumed";
    public const string GrantExpired = "grant.expired";
    public const string CredentialAccessed = "credential.accessed";
    public const string CredentialAccessDenied = "credential.access-denied";
    public const string AgentEnrolled = "agent.enrolled";
    public const string AgentBlocked = "agent.blocked";
    public const string AgentReactivated = "agent.reactivated";
    public const string AgentDeleted = "agent.deleted";
    public const string VaultCreated = "vault.created";
    public const string VaultUpdated = "vault.updated";
    public const string VaultDeleted = "vault.deleted";
    public const string VaultExported = "vault.exported";
    public const string EntryCreated = "entry.created";
    public const string EntryUpdated = "entry.updated";
    public const string EntryDeleted = "entry.deleted";
    public const string OrganizationCreated = "org.created";
    public const string OrganizationUpdated = "org.updated";
    public const string OrganizationMemberInvited = "org.member-invited";
    public const string OrganizationMemberJoined = "org.member-joined";
    public const string OrganizationMemberRoleChanged = "org.member-role-changed";
    public const string OrganizationMemberRemoved = "org.member-removed";
    public const string UserSignedUp = "user.signed-up";
    public const string AccountSetupCompleted = "account.setup-completed";
    public const string AccountRecoveryCompleted = "account.recovery-completed";
    public const string ApiKeyCreated = "apikey.created";
    public const string ApiKeyActivated = "apikey.activated";
    public const string ApiKeyRevoked = "apikey.revoked";
    public const string ApiKeyDeleted = "apikey.deleted";
    public const string ExportRequested = "audit.export.requested";
}
