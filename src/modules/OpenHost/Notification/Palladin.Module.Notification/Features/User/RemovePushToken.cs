using Palladin.Core.Events;
using Palladin.Core.Security;
using Palladin.Module.Notification.Contracts.Events;
using Palladin.Module.Notification.Infrastructure.Persistence;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Palladin.Module.Notification.Features;

[PublicAPI]
public sealed record RemovePushTokenRequest
{
    public Guid TokenId { get; init; }
}

[UsedImplicitly]
internal sealed class RemovePushTokenValidator : Validator<RemovePushTokenRequest>
{
    public RemovePushTokenValidator()
    {
        RuleFor(x => x.TokenId).NotEmpty();
    }
}

[PublicAPI]
[AllowNonActiveOrganizationMembership]
internal sealed class RemovePushTokenEndpoint(
    NotificationDomainWriteContext domainWriteContext,
    IEnumerable<IEventPublisher> eventPublishers,
    IClock clock) : Endpoint<RemovePushTokenRequest>
{
    public override void Configure()
    {
        Delete("api/push-tokens/{tokenId:guid}");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Summary(summary =>
        {
            summary.Summary = "Remove a device push token";
            summary.Description = "Removes one of the current user's push tokens (device sign-out / management). Returns 404 if the token does not exist or belongs to another user.";
        });
        Tags("Notification/PushTokens");
    }

    public override async Task HandleAsync(RemovePushTokenRequest req, CancellationToken ct)
    {
        var userId = User.GetUserId()!.Value;

        var deleted = await domainWriteContext.PushTokens
            .Where(t => t.Id == req.TokenId && t.UserId == userId)
            .ExecuteDeleteAsync(ct);

        if (deleted == 0)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Analytics flow per CLAUDE.md: domain event -> MassTransit trigger -> analytics publish.
        // Never call analyticsService.CaptureEvent directly from an endpoint.
        foreach (var publisher in eventPublishers)
        {
            await publisher.PublishAsync(
                new PushTokenRemovedEvent(req.TokenId, userId, clock.GetCurrentInstant()),
                ct);
        }

        await Send.NoContentAsync(ct);
    }
}
