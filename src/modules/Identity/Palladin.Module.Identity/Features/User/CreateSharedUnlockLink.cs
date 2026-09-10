using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;
using Palladin.Core.Api;
using Palladin.Core.Security;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.Persistence;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record CreateSharedUnlockLinkRequest
{
    public Guid LinkId { get; init; }
    public uint ExpectedPreferenceRevision { get; init; }
}

[UsedImplicitly]
internal sealed class CreateSharedUnlockLinkValidator : Validator<CreateSharedUnlockLinkRequest>
{
    public CreateSharedUnlockLinkValidator()
    {
        RuleFor(request => request.LinkId).NotEmpty();
        RuleFor(request => request.ExpectedPreferenceRevision).GreaterThan(0u);
    }
}

[PublicAPI]
internal sealed class CreateSharedUnlockLinkEndpoint(IdentityDomainWriteContext context, IClock clock)
    : Endpoint<CreateSharedUnlockLinkRequest, SharedUnlockLinkResponse>
{
    public override void Configure()
    {
        Post("api/account/shared-unlock/links");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Tags("Identity/Account");
        Summary(summary => summary.Summary = "Create a locked local link after verified browser discovery");
    }

    public override async Task HandleAsync(CreateSharedUnlockLinkRequest req, CancellationToken ct)
    {
        var userId = User.GetUserId();
        var user = await context.Users.SingleOrDefaultAsync(user => user.Id == userId, ct);
        if (user is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        if (!user.SharedUnlockEnabled || user.SharedUnlockRevision != req.ExpectedPreferenceRevision)
        {
            AddError(ErrorResponses.General("shared-unlock-preference-conflict"));
            await Send.ErrorsAsync(StatusCodes.Status409Conflict, ct);
            return;
        }

        var link = SharedUnlockLink.Create(user.Id, req.LinkId, clock.GetCurrentInstant());
        context.Add(link);
        context.MarkPropertyAsUpdated(user, current => current.SharedUnlockRevision);
        try
        {
            await context.CommitAsync(ct);
        }
        catch (Exception exception) when (exception is DbUpdateConcurrencyException
            || exception is DbUpdateException { InnerException: PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "PK_SharedUnlockLinks" } })
        {
            AddError(ErrorResponses.General("shared-unlock-link-conflict"));
            await Send.ErrorsAsync(StatusCodes.Status409Conflict, ct);
            return;
        }

        HttpContext.Response.Headers.CacheControl = "no-store";
        await Send.OkAsync(new SharedUnlockLinkResponse(link.Id, link.Revision, link.Epoch, "locked"), ct);
    }
}
