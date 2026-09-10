using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Api;
using Palladin.Core.Security;
using Palladin.Module.Identity.Infrastructure;
using Palladin.Module.Identity.Infrastructure.Persistence;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record LockSharedUnlockLinkRequest
{
    public Guid LinkId { get; init; }
    public uint ExpectedRevision { get; init; }
    public uint ExpectedPreferenceRevision { get; init; }
}

[UsedImplicitly]
internal sealed class LockSharedUnlockLinkValidator : Validator<LockSharedUnlockLinkRequest>
{
    public LockSharedUnlockLinkValidator()
    {
        RuleFor(request => request.LinkId).NotEmpty();
        RuleFor(request => request.ExpectedRevision).GreaterThan(0u);
        RuleFor(request => request.ExpectedPreferenceRevision).GreaterThan(0u);
    }
}

[PublicAPI]
internal sealed class LockSharedUnlockLinkEndpoint(IdentityDomainWriteContext context, IClock clock)
    : Endpoint<LockSharedUnlockLinkRequest, SharedUnlockLinkResponse>
{
    public override void Configure()
    {
        Post("api/account/shared-unlock/links/{LinkId}/lock");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Options(builder => builder.AllowNonActiveOrganizationMembership());
        Tags("Identity/Account");
        Summary(summary => summary.Summary = "Lock the linked browser clients and invalidate their previous epoch");
    }

    public override async Task HandleAsync(LockSharedUnlockLinkRequest req, CancellationToken ct)
    {
        var userId = User.GetUserId();
        var link = await context.SharedUnlockLinks.SingleOrDefaultAsync(link => link.UserId == userId && link.Id == req.LinkId, ct);
        if (link is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var user = await context.Users.SingleOrDefaultAsync(user => user.Id == userId, ct);
        if (user is null || !user.SharedUnlockEnabled || user.SharedUnlockRevision != req.ExpectedPreferenceRevision)
        {
            AddError(ErrorResponses.General("shared-unlock-preference-conflict"));
            await Send.ErrorsAsync(StatusCodes.Status409Conflict, ct);
            return;
        }

        context.MarkPropertyAsUpdated(user, current => current.SharedUnlockRevision);

        if (!user.TryAdvanceSharedUnlockSequence()
            || !link.TryLock(req.ExpectedRevision, user.SharedUnlockSequence, clock.GetCurrentInstant()))
        {
            AddError(ErrorResponses.General("shared-unlock-link-conflict"));
            await Send.ErrorsAsync(StatusCodes.Status409Conflict, ct);
            return;
        }

        try
        {
            await context.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            AddError(ErrorResponses.General("shared-unlock-link-conflict"));
            await Send.ErrorsAsync(StatusCodes.Status409Conflict, ct);
            return;
        }

        HttpContext.Response.Headers.CacheControl = "no-store";
        await Send.OkAsync(new SharedUnlockLinkResponse(link.Id, link.Revision, link.Epoch,
            link.State.ToString().ToLowerInvariant()), ct);
    }
}
