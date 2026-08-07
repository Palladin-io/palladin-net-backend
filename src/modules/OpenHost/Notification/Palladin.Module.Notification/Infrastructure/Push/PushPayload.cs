using Palladin.Core.Types;
using Palladin.Module.Notification.Shared;
using NodaTime.Text;

namespace Palladin.Module.Notification.Infrastructure.Push;

internal static class PushPayload
{
    internal static IReadOnlyDictionary<string, string> Build(PushDispatch dispatch) =>
        new Dictionary<string, string>
        {
            ["type"] = dispatch.Type.ToWire(),
            ["category"] = dispatch.Category == NotificationCategory.ActionRequired ? "actionRequired" : "update",
            ["subjectId"] = dispatch.SubjectId.ToString(),
            ["occurredAt"] = InstantPattern.ExtendedIso.Format(dispatch.OccurredAt),
        };
}
