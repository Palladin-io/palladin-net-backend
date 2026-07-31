using Palladin.Core.Security;
using Palladin.Module.Identity.Infrastructure.Persistence;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record UpdateOrgRequest
{
    public string Name { get; init; } = string.Empty;
}

[UsedImplicitly]
internal sealed class UpdateOrgValidator : Validator<UpdateOrgRequest>
{
    public UpdateOrgValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
    }
}

[PublicAPI]
internal sealed class UpdateOrgEndpoint(
    IdentityDomainWriteContext domainWriteContext,
    IClock clock) : Endpoint<UpdateOrgRequest>
{
    public override void Configure()
    {
        Put("api/org");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.OrganizationManagement);
        Summary(summary =>
        {
            summary.Summary = "Update organization settings";
            summary.Description = "Updates the name of the authenticated user's organization.";
        });
        Tags("Identity/Organization");
    }

    public override async Task HandleAsync(UpdateOrgRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId();
        var userId = User.GetUserId();
        if (organizationId is null || userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var org = await domainWriteContext.Organizations
            .FirstOrDefaultAsync(o => o.Id == organizationId, ct);

        if (org is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var actorName = await domainWriteContext.Users
            .Where(u => u.Id == userId)
            .Select(u => u.DisplayName)
            .FirstOrDefaultAsync(ct) ?? string.Empty;

        org.UpdateName(req.Name.Trim(), userId.Value, actorName, clock.GetCurrentInstant());
        await domainWriteContext.CommitAsync(ct);

        await Send.NoContentAsync(ct);
    }
}
