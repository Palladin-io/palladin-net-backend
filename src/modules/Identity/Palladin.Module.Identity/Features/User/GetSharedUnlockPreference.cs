using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Palladin.Core.Security;
using Palladin.Module.Identity.Infrastructure.Persistence;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record SharedUnlockPreferenceResponse(bool SharedUnlockEnabled, uint Revision);

[PublicAPI]
internal sealed class GetSharedUnlockPreferenceEndpoint(IdentityDomainReadContext domainReadContext)
    : EndpointWithoutRequest<SharedUnlockPreferenceResponse>
{
    public override void Configure()
    {
        Get("api/account/shared-unlock");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Tags("Identity/Account");
        Summary(summary =>
        {
            summary.Summary = "Get the current user's shared-unlock preference";
            summary.Description = "Returns the account-wide choice and its revision, independent of the selected organization or local browser link.";
        });
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var preference = await domainReadContext.Users
            .Where(user => user.Id == userId.Value)
            .Select(user => new SharedUnlockPreferenceResponse(user.SharedUnlockEnabled, user.SharedUnlockRevision))
            .SingleOrDefaultAsync(ct);
        if (preference is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        HttpContext.Response.Headers.CacheControl = "no-store";
        await Send.OkAsync(preference, ct);
    }
}
