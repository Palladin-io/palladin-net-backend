using NodaTime;
using Palladin.Core.Types;
using Palladin.Module.Notification.Infrastructure.Push;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Notification;

public sealed class PushPayloadTests
{
    [Fact]
    public void CanonicalPushPayload_ContainsOnlyGenericRoutingFields()
    {
        var subjectId = Guid.NewGuid();
        var occurredAt = Instant.FromUtc(2026, 7, 25, 20, 30);

        var payload = PushPayload.Build(new PushDispatch(
            subjectId,
            NotificationType.GrantPending,
            NotificationCategory.ActionRequired,
            occurredAt));

        payload.ShouldBe(new Dictionary<string, string>
        {
            ["type"] = "grant_pending",
            ["category"] = "actionRequired",
            ["subjectId"] = subjectId.ToString(),
            ["occurredAt"] = "2026-07-25T20:30:00Z",
        });
        payload.Keys.ShouldNotContain(key =>
            new[] { "vaultName", "entryLabel", "agentName", "reason", "account" }.Contains(key));
    }
}
