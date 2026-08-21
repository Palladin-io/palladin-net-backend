using Palladin.Core.Events;
using Palladin.Core.Guid;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Notification.Domain;
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
public sealed record RegisterPushTokenRequest
{
    public string Token { get; init; } = string.Empty;
    public PushPlatform Platform { get; init; }
    public string? DeviceName { get; init; }
}

[PublicAPI]
public sealed record RegisterPushTokenResponse(Guid Id);

[UsedImplicitly]
internal sealed class RegisterPushTokenValidator : Validator<RegisterPushTokenRequest>
{
    public RegisterPushTokenValidator()
    {
        RuleFor(x => x.Token).NotEmpty().MaximumLength(4096);
        RuleFor(x => x.Platform).IsInEnum();
        RuleFor(x => x.DeviceName!).MaximumLength(200).When(x => x.DeviceName is not null);
    }
}

[PublicAPI]
internal sealed class RegisterPushTokenEndpoint(
    NotificationDomainWriteContext domainWriteContext,
    IGuidProvider guidProvider,
    IClock clock,
    IEnumerable<IEventPublisher> eventPublishers) : Endpoint<RegisterPushTokenRequest, RegisterPushTokenResponse>
{
    public override void Configure()
    {
        Post("api/push-tokens");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Options(builder => builder.AllowNonActiveOrganizationMembership());
        Summary(summary =>
        {
            summary.Summary = "Register a device push token";
            summary.Description = "Registers (or refreshes) a device push token for the current user. Works for iOS, Android and Web (FCM for Web). Re-registering the same token refreshes its metadata instead of creating a duplicate.";
        });
        Tags("Notification/PushTokens");
    }

    public override async Task HandleAsync(RegisterPushTokenRequest req, CancellationToken ct)
    {
        var userId = User.GetUserId()!.Value;
        var organizationId = User.GetOrganizationId()!.Value;
        var now = clock.GetCurrentInstant();

        // Look up by token alone — a device token is globally unique (per-installation). If it exists
        // (possibly under another user after a device hand-off) reassign it to the current registrant.
        var existing = await domainWriteContext.PushTokens
            .FirstOrDefaultAsync(t => t.Token == req.Token, ct);

        Guid tokenId;
        if (existing is not null)
        {
            // EF already tracks `existing` from the FirstOrDefaultAsync above — mutating it through
            // Reassign is picked up by the change tracker at SaveChanges. An explicit Update() would
            // re-mark every property as modified and issue a wider UPDATE.
            existing.Reassign(userId, organizationId, req.Platform, req.DeviceName, now);
            tokenId = existing.Id;
        }
        else
        {
            var pushToken = PushToken.Create(
                guidProvider.Generate(), userId, organizationId, req.Token, req.Platform, req.DeviceName, now);
            domainWriteContext.Add(pushToken);
            tokenId = pushToken.Id;
        }

        await domainWriteContext.CommitAsync(ct);

        // Analytics flow per CLAUDE.md: domain event -> MassTransit trigger -> analytics publish.
        // Never call analyticsService.CaptureEvent directly from an endpoint.
        foreach (var publisher in eventPublishers)
        {
            await publisher.PublishAsync(
                new PushTokenRegisteredEvent(tokenId, userId, organizationId, req.Platform, now),
                ct);
        }

        await Send.OkAsync(new RegisterPushTokenResponse(tokenId), ct);
    }
}
