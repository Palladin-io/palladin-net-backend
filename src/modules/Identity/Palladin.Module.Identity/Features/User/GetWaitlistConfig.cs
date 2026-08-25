using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using NodaTime;
using Palladin.Module.Identity.Infrastructure.Waitlist;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record GetWaitlistConfigResponse(
    bool AcceptingSignups,
    DateTimeOffset? PublicLaunchAtUtc,
    DateTimeOffset? BenefitClaimDeadlineAtUtc,
    string PromotionTermsVersion);

[PublicAPI]
internal sealed class GetWaitlistConfigEndpoint(
    IOptions<WaitlistOptions> options,
    IClock clock) : EndpointWithoutRequest<GetWaitlistConfigResponse>
{
    public override void Configure()
    {
        Get("api/waitlist/config");
        AllowAnonymous();
        Summary(summary =>
        {
            summary.Summary = "Get public waitlist configuration";
            summary.Description =
                "Returns the public launch-benefit dates, terms version and whether pre-launch signups are open.";
        });
        Tags("Identity/Waitlist");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var configured = options.Value;
        var publicLaunch = configured.PublicLaunchAtUtc;
        var acceptingSignups = configured.Enabled
            && configured.BenefitEnabled
            && publicLaunch is not null
            && clock.GetCurrentInstant() < Instant.FromDateTimeOffset(publicLaunch.Value);

        HttpContext.Response.Headers.CacheControl = "no-store";
        await Send.OkAsync(new GetWaitlistConfigResponse(
            acceptingSignups,
            publicLaunch,
            configured.BenefitClaimDeadlineAtUtc,
            configured.PromotionTermsVersion), ct);
    }
}
