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
public sealed record ReconnectSharedUnlockLinkRequest
{
    public Guid LinkId { get; init; }
    public uint ExpectedRevision { get; init; }
}

[UsedImplicitly]
internal sealed class ReconnectSharedUnlockLinkValidator : Validator<ReconnectSharedUnlockLinkRequest>
{
    public ReconnectSharedUnlockLinkValidator()
    {
        RuleFor(request => request.LinkId).NotEmpty();
        RuleFor(request => request.ExpectedRevision).GreaterThan(0u);
    }
}

[PublicAPI]
internal sealed class ReconnectSharedUnlockLinkEndpoint(IdentityDomainWriteContext context, IClock clock)
    : Endpoint<ReconnectSharedUnlockLinkRequest, SharedUnlockLinkResponse>
{
    public override void Configure()
    {
        Post("api/account/shared-unlock/links/{LinkId}/reconnect");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Tags("Identity/Account");
        Summary(summary => summary.Summary = "Explicitly reconnect a revoked link in a locked new epoch");
    }

    public override async Task HandleAsync(ReconnectSharedUnlockLinkRequest req, CancellationToken ct)
    {
        var userId = User.GetUserId();
        var link = await context.SharedUnlockLinks.SingleOrDefaultAsync(link => link.UserId == userId && link.Id == req.LinkId, ct);
        if (link is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }


        if (!link.TryReconnect(req.ExpectedRevision, clock.GetCurrentInstant()))
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
