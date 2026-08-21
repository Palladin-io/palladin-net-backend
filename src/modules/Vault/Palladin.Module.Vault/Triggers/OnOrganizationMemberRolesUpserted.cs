using JetBrains.Annotations;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Infrastructure.MassTransit;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Triggers;

[UsedImplicitly]
internal sealed class OnOrganizationMemberRolesUpsertedDefinition
    : ConsumerDefinition<OnOrganizationMemberRolesUpserted>
{
    public OnOrganizationMemberRolesUpsertedDefinition() => EndpointName = VaultEndpoints.FromIdentity;
}

[UsedImplicitly]
internal sealed class OnOrganizationMemberRolesUpserted(
    VaultDomainWriteContext domainWriteContext,
    RoleVaultAccessReconciler reconciler)
    : IConsumer<OrganizationMemberRolesUpsertedEvent>
{
    public async Task Consume(ConsumeContext<OrganizationMemberRolesUpsertedEvent> context)
    {
        var message = context.Message;
        var roleSet = await domainWriteContext.OrganizationMemberRoleSets.FirstOrDefaultAsync(
            x => x.OrganizationId == message.OrganizationId && x.UserId == message.UserId,
            context.CancellationToken);
        if (roleSet is null)
        {
            domainWriteContext.Add(OrganizationMemberRoleSet.Create(
                message.OrganizationId,
                message.UserId,
                message.RoleIds,
                message.Revision,
                message.AuthorizationVersion,
                message.IsActive,
                message.UpdatedAt));
            await reconciler.ReconcileMemberRoleSetAsync(
                message.OrganizationId,
                message.UserId,
                [],
                wasActive: false,
                message.RoleIds,
                message.IsActive,
                message.UpdatedAt,
                context.CancellationToken);
            await domainWriteContext.CommitAsync(context.CancellationToken);
            return;
        }

        var previousRoleIds = roleSet.RoleIds.ToArray();
        var wasActive = roleSet.IsActive;
        if (roleSet.Apply(
                message.RoleIds,
                message.Revision,
                message.AuthorizationVersion,
                message.IsActive,
                message.UpdatedAt))
        {
            await reconciler.ReconcileMemberRoleSetAsync(
                message.OrganizationId,
                message.UserId,
                previousRoleIds,
                wasActive,
                message.RoleIds,
                message.IsActive,
                message.UpdatedAt,
                context.CancellationToken);
            await domainWriteContext.CommitAsync(context.CancellationToken);
        }
    }
}
