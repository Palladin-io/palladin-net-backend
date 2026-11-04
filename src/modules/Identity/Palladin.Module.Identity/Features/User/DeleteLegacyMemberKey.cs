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
public sealed record DeleteLegacyMemberKeyRequest
{
    [QueryParam] public uint LegacyMemberKeyVersion { get; init; }
}

[UsedImplicitly]
internal sealed class DeleteLegacyMemberKeyValidator : Validator<DeleteLegacyMemberKeyRequest>
{
    public DeleteLegacyMemberKeyValidator() => RuleFor(x => x.LegacyMemberKeyVersion).GreaterThan(0u);
}

[PublicAPI]
internal sealed class DeleteLegacyMemberKeyEndpoint(IdentityDomainWriteContext domainWriteContext, IClock clock)
    : Endpoint<DeleteLegacyMemberKeyRequest>
{
    public override void Configure()
    {
        Delete("api/account/legacy-member-key");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Options(builder => builder.AllowNonActiveOrganizationMembership());
        Summary(summary =>
        {
            summary.Summary = "Delete the wrapped legacy Member private key";
            summary.Description = "Removes the opaque wrapped private key kept after a security upgrade. The client "
                + "calls this only after GET api/account/key-migration reports zero pending Member envelopes in every "
                + "organization; afterwards envelopes still addressed to the legacy key can no longer be opened. "
                + "A missing record returns 404 and a different legacy version returns 409.";
        });
        Tags("Identity/Account");
    }

    public override async Task HandleAsync(DeleteLegacyMemberKeyRequest req, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var user = await domainWriteContext.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user?.LegacyMemberKeyVersion is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (!user.DeleteLegacyMemberKey(req.LegacyMemberKeyVersion, clock.GetCurrentInstant()))
        {
            AddError(ErrorResponses.General("legacy-member-key-version-mismatch"));
            await Send.ErrorsAsync(StatusCodes.Status409Conflict, ct);
            return;
        }

        await domainWriteContext.CommitAsync(ct);
        await Send.NoContentAsync(ct);
    }
}
