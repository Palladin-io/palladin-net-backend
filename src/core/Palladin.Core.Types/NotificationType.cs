using System.Text.Json.Serialization;

namespace Palladin.Core.Types;

public enum NotificationType
{
    [JsonStringEnumMemberName("agent_pending")]
    AgentPending = 1,

    [JsonStringEnumMemberName("grant_pending")]
    GrantPending = 2,

    [JsonStringEnumMemberName("grant_revoked")]
    GrantRevoked = 3,

    [JsonStringEnumMemberName("grant_approved")]
    GrantApproved = 4,

    [JsonStringEnumMemberName("grant_denied")]
    GrantDenied = 5,

    [JsonStringEnumMemberName("credential_stale")]
    CredentialStale = 6,

    [JsonStringEnumMemberName("agent_resolved")]
    AgentResolved = 7,

    [JsonStringEnumMemberName("agent_approved")]
    AgentApproved = 8,

    [JsonStringEnumMemberName("agent_deactivated")]
    AgentDeactivated = 9,
}
