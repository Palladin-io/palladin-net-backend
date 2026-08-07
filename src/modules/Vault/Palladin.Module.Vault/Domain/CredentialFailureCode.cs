using System.Text.Json.Serialization;
using JetBrains.Annotations;

namespace Palladin.Module.Vault.Domain;

[PublicAPI]
public enum CredentialFailureCode
{
    [JsonStringEnumMemberName("login_rejected")]
    LoginRejected = 1,

    [JsonStringEnumMemberName("auth_failed")]
    AuthFailed = 2,

    [JsonStringEnumMemberName("manual")]
    Manual = 3,
}
