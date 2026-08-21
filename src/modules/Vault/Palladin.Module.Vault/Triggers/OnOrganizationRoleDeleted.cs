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
internal sealed class OnOrganizationRoleDeletedDefinition : ConsumerDefinition<OnOrganizationRoleDeleted>
{
    public OnOrganizationRoleDeletedDefinition() => EndpointName = VaultEndpoints.FromIdentity;
}

[UsedImplicitly]
internal sealed class OnOrganizationRoleDeleted(
    VaultDomainWriteContext domainWriteContext,
    RoleVaultAccessReconciler reconciler)
    : IConsumer<OrganizationRoleDeletedEvent>
{
    public async Task Consume(ConsumeContext<OrganizationRoleDeletedEvent> context)
    {
        var message = context.Message;
        var role = await domainWriteContext.OrganizationRoleDirectory.FirstOrDefaultAsync(
            x => x.OrganizationId == message.OrganizationId && x.RoleId == message.RoleId,
            context.CancellationToken);
        if (role is null)
        {
            role = OrganizationRoleDirectoryEntry.CreateDeleted(
                message.OrganizationId,
                message.RoleId,
                message.Revision,
                message.IsSystem,
                message.Permissions,
                message.DeletedAt);
            domainWriteContext.Add(role);
        }
        else if (!role.ApplyDeletion(
                     message.Revision,
                     message.IsSystem,
                     message.Permissions,
                     message.DeletedAt))
        {
            return;
        }

        await reconciler.ReconcileRoleDeletionAsync(
            message.OrganizationId,
            message.RoleId,
            message.DeletedAt,
            context.CancellationToken);

        await domainWriteContext.CommitAsync(context.CancellationToken);
    }
}
