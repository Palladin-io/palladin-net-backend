using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Security;
using Palladin.Module.Identity.Contracts.ValueObjects;
using Palladin.Module.Identity.Infrastructure.Persistence;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record UpdateOrganizationOfflineAccessPolicyRequest
{
    public OrganizationOfflineAccessPolicy? Policy { get; init; }
}

[PublicAPI]
public sealed record UpdateOrganizationOfflineAccessPolicyResponse(
    OrganizationOfflineAccessPolicy Policy,
    uint PolicyVersion);

[UsedImplicitly]
internal sealed class UpdateOrganizationOfflineAccessPolicyValidator
    : Validator<UpdateOrganizationOfflineAccessPolicyRequest>
{
    public UpdateOrganizationOfflineAccessPolicyValidator()
    {
        RuleFor(x => x.Policy).NotNull().IsInEnum();
    }
}

[PublicAPI]
internal sealed class UpdateOrganizationOfflineAccessPolicyEndpoint(
    IdentityDomainWriteContext domainWriteContext,
    IClock clock)
    : Endpoint<UpdateOrganizationOfflineAccessPolicyRequest,
        UpdateOrganizationOfflineAccessPolicyResponse>
{
    public override void Configure()
    {
        Put("api/org/offline-access-policy");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.OrganizationManagement);
        Summary(summary =>
        {
            summary.Summary = "Update the organization offline-access policy";
            summary.Description =
                "Selects one frozen finite policy. A changed policy invalidates existing access tokens, which must be refreshed before further authenticated requests.";
        });
        Tags("Identity/Organization");
    }

    public override async Task HandleAsync(
        UpdateOrganizationOfflineAccessPolicyRequest req,
        CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId();
        var userId = User.GetUserId();
        if (organizationId is null || userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var organization = await domainWriteContext.Organizations
            .SingleOrDefaultAsync(x => x.Id == organizationId.Value, ct);
        if (organization is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var actorName = await domainWriteContext.Users
            .Where(x => x.Id == userId.Value)
            .Select(x => x.DisplayName)
            .SingleOrDefaultAsync(ct) ?? string.Empty;
        organization.SetOfflineAccessPolicy(
            req.Policy!.Value,
            userId.Value,
            actorName,
            clock.GetCurrentInstant());
        await domainWriteContext.CommitAsync(ct);

        await Send.OkAsync(new UpdateOrganizationOfflineAccessPolicyResponse(
            organization.OfflineAccessPolicy,
            organization.OfflineAccessPolicyVersion), ct);
    }
}
