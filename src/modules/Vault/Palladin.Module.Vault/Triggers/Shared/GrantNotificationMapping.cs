using Palladin.Core.Types;

namespace Palladin.Module.Vault.Triggers;

internal static class GrantNotificationMapping
{
    internal const string GrantPendingTitleKey = "notification.grant_pending.title";
    internal const string GrantApprovedTitleKey = "notification.grant_approved.title";
    internal const string GrantDeniedTitleKey = "notification.grant_denied.title";
    internal const string CredentialStaleTitleKey = "notification.credential_stale.title";

    internal const string ApproveActionType = "approve_grant";
    internal const string ViewGrantActionType = "view_grant";
    internal const string ViewEntryActionType = "view_entry";

    internal static string GrantTypeWire(GrantType type) =>
        type switch
        {
            GrantType.Full => "full",
            GrantType.ScriptExecution => "script_execution",
            _ => "granular",
        };

    internal static string CredentialFailureErrorHint(string code) => code switch
    {
        "login_rejected" => "login_rejected",
        "auth_failed" => "auth_failed",
        _ => "manual",
    };
}
