using Palladin.Core.Security;
using Palladin.Module.Vault.Infrastructure.Persistence;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Palladin.Module.Vault.Infrastructure.Authorization;

internal sealed class RequireVaultMembershipPreProcessor<TRequest> : IPreProcessor<TRequest>
    where TRequest : IRequiresVaultMembership
{
    public async Task PreProcessAsync(IPreProcessorContext<TRequest> context, CancellationToken ct)
    {
        if (context.HasValidationFailures)
        {
            return;
        }

        var userId = context.HttpContext.User.GetUserId();
        var organizationId = context.HttpContext.User.GetOrganizationId();
        if (userId is null || organizationId is null)
        {
            await context.HttpContext.Response.SendUnauthorizedAsync(ct);
            return;
        }

        var readContext = context.HttpContext.RequestServices.GetRequiredService<VaultDbReadContext>();
        var vaultId = context.Request!.VaultId;
        var isMember = await readContext.Vaults.AnyAsync(v =>
            v.OrganizationId == organizationId
            && v.Id == vaultId
            && v.VaultMembers.Any(m => m.UserId == userId), ct);

        if (!isMember)
        {
            await context.HttpContext.Response.SendForbiddenAsync(ct);
        }
    }
}
