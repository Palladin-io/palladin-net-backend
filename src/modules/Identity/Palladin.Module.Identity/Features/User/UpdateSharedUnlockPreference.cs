using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Api;
using Palladin.Core.Security;
using Palladin.Module.Identity.Infrastructure.Persistence;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record UpdateSharedUnlockPreferenceRequest
{
    public bool? SharedUnlockEnabled { get; init; }
    public uint? ExpectedRevision { get; init; }
}

[UsedImplicitly]
internal sealed class UpdateSharedUnlockPreferenceValidator : Validator<UpdateSharedUnlockPreferenceRequest>
{
    public UpdateSharedUnlockPreferenceValidator()
    {
        RuleFor(request => request.SharedUnlockEnabled).NotNull();
        RuleFor(request => request.ExpectedRevision).NotNull().GreaterThan(0u);
    }
}

[PublicAPI]
internal sealed class UpdateSharedUnlockPreferenceEndpoint(
    IdentityDomainWriteContext domainWriteContext,
    IClock clock)
    : Endpoint<UpdateSharedUnlockPreferenceRequest, SharedUnlockPreferenceResponse>
{
    public override void Configure()
    {
        Put("api/account/shared-unlock");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Tags("Identity/Account");
        Summary(summary =>
        {
            summary.Summary = "Update the current user's shared-unlock preference";
            summary.Description = "Requires the last observed revision. Every accepted choice advances the revision, including an unchanged value; stale or concurrent writes return 409.";
        });
    }

    public override async Task HandleAsync(UpdateSharedUnlockPreferenceRequest req, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var user = await domainWriteContext.Users.SingleOrDefaultAsync(user => user.Id == userId.Value, ct);
        if (user is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        if (!user.TrySetSharedUnlockPreference(req.SharedUnlockEnabled!.Value, req.ExpectedRevision!.Value, clock.GetCurrentInstant()))
        {
            AddError(ErrorResponses.General("shared-unlock-preference-conflict"));
            await Send.ErrorsAsync(StatusCodes.Status409Conflict, ct);
            return;
        }

        try
        {
            await domainWriteContext.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            AddError(ErrorResponses.General("shared-unlock-preference-conflict"));
            await Send.ErrorsAsync(StatusCodes.Status409Conflict, ct);
            return;
        }

        HttpContext.Response.Headers.CacheControl = "no-store";
        await Send.OkAsync(new SharedUnlockPreferenceResponse(user.SharedUnlockEnabled, user.SharedUnlockRevision), ct);
    }
}
