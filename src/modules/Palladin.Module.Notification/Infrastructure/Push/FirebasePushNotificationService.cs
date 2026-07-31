using Palladin.Core.Events;
using Palladin.Core.Types;
using Palladin.Module.Notification.Contracts.Events;
using Palladin.Module.Notification.Infrastructure.Persistence;
using Palladin.Module.Notification.Shared;
using FirebaseAdmin.Messaging;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using FcmNotification = FirebaseAdmin.Messaging.Notification;

namespace Palladin.Module.Notification.Infrastructure.Push;

[UsedImplicitly]
internal sealed class FirebasePushNotificationService(
    IFirebaseMessagingProvider messagingProvider,
    NotificationDbReadContext readContext,
    NotificationDbWriteContext writeContext,
    IOptions<FirebaseOptions> options,
    PushText pushText,
    IEnumerable<IEventPublisher> eventPublishers,
    IClock clock,
    ILogger<FirebasePushNotificationService> logger) : IPushNotificationService
{
    public async Task SendToUsersAsync(
        Guid organizationId,
        IReadOnlyCollection<Guid> userIds,
        PushDispatch dispatch,
        CancellationToken ct)
    {
        var messaging = messagingProvider.Messaging;
        if (messaging is null || userIds.Count == 0)
        {
            return;
        }

        var tokens = await readContext.PushTokens
            .Where(t => t.OrganizationId == organizationId && userIds.Contains(t.UserId))
            .Select(t => new { t.Token, t.Platform })
            .ToListAsync(ct);

        if (tokens.Count == 0)
        {
            return;
        }

        // System-level notifications must remain generic while the client is locked. Metadata is
        // deliberately not interpolated into lock-screen/browser text; the app resolves opaque ids
        // after unlock.
        var (title, body) = pushText.For(dispatch.Type, null);
        var messages = tokens.Select(t => BuildMessage(t.Token, t.Platform, dispatch, title, body)).ToList();
        var response = await messaging.SendEachAsync(messages, ct);

        foreach (var publisher in eventPublishers)
        {
            await publisher.PublishAsync(
                new PushNotificationSentEvent(
                    organizationId, dispatch.Type.ToWire(), response.SuccessCount, response.FailureCount, clock.GetCurrentInstant()),
                ct);
        }

        await CleanupStaleTokensAsync(tokens.Select(t => t.Token).ToList(), response, ct);
    }

    private Message BuildMessage(string token, PushPlatform platform, PushDispatch dispatch, string title, string body)
    {
        var message = new Message
        {
            Token = token,
            Notification = new FcmNotification { Title = title, Body = body },
            Data = new Dictionary<string, string>(PushPayload.Build(dispatch)),
        };

        if (platform == PushPlatform.Web)
        {
            message.Webpush = new WebpushConfig
            {
                FcmOptions = new WebpushFcmOptions { Link = options.Value.WebClickBaseUrl },
            };
        }

        return message;
    }

    private async Task CleanupStaleTokensAsync(
        IReadOnlyList<string> tokens,
        BatchResponse response,
        CancellationToken ct)
    {
        var staleTokens = new List<string>();
        for (var i = 0; i < response.Responses.Count; i++)
        {
            var sendResponse = response.Responses[i];
            if (sendResponse.IsSuccess)
            {
                continue;
            }

            var errorCode = sendResponse.Exception?.MessagingErrorCode;
            if (errorCode is MessagingErrorCode.Unregistered or MessagingErrorCode.SenderIdMismatch)
            {
                staleTokens.Add(tokens[i]);
            }
        }

        if (staleTokens.Count == 0)
        {
            return;
        }

        var removed = await writeContext.PushTokens
            .Where(t => staleTokens.Contains(t.Token))
            .ExecuteDeleteAsync(ct);

        logger.LogInformation("Removed {StaleTokenCount} stale push token(s)", removed);
    }
}
