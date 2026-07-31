using System.Text.Json;
using Palladin.Core.Json;
using Palladin.Core.Types;

namespace Palladin.Tests.Unit.Core.Types;

public sealed class NotificationEnumWireTests
{
    private static readonly JsonSerializerOptions Options =
        PalladinJsonSerializationSettings.AddPalladinDefaultConfiguration(new JsonSerializerOptions());

    [Theory]
    [InlineData(NotificationType.AgentPending, "agent_pending")]
    [InlineData(NotificationType.GrantPending, "grant_pending")]
    [InlineData(NotificationType.GrantRevoked, "grant_revoked")]
    [InlineData(NotificationType.GrantApproved, "grant_approved")]
    [InlineData(NotificationType.GrantDenied, "grant_denied")]
    [InlineData(NotificationType.CredentialStale, "credential_stale")]
    [InlineData(NotificationType.AgentResolved, "agent_resolved")]
    [InlineData(NotificationType.AgentApproved, "agent_approved")]
    [InlineData(NotificationType.AgentDeactivated, "agent_deactivated")]
    public void When_SerializingNotificationType_Then_WireValueIsSnakeCase(NotificationType type, string expected)
    {
        JsonSerializer.Serialize(type, Options).ShouldBe($"\"{expected}\"");
        JsonSerializer.Deserialize<NotificationType>($"\"{expected}\"", Options).ShouldBe(type);
    }

    [Theory]
    [InlineData(NotificationCategory.ActionRequired, "actionRequired")]
    [InlineData(NotificationCategory.Update, "update")]
    public void When_SerializingNotificationCategory_Then_WireValueMatches(NotificationCategory category, string expected)
    {
        JsonSerializer.Serialize(category, Options).ShouldBe($"\"{expected}\"");
        JsonSerializer.Deserialize<NotificationCategory>($"\"{expected}\"", Options).ShouldBe(category);
    }
}
