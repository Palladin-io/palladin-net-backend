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
internal sealed class OnOrganizationRoleUpsertedDefinition : ConsumerDefinition<OnOrganizationRoleUpserted>
{
    public OnOrganizationRoleUpsertedDefinition() => EndpointName = VaultEndpoints.FromIdentity;
}

[UsedImplicitly]
internal sealed class OnOrganizationRoleUpserted(
    VaultDomainWriteContext domainWriteContext,
    RoleVaultAccessReconciler reconciler)
    : IConsumer<OrganizationRoleUpsertedEvent>
{
    public async Task Consume(ConsumeContext<OrganizationRoleUpsertedEvent> context)
    {
        var message = context.Message;
        var role = await domainWriteContext.OrganizationRoleDirectory.FirstOrDefaultAsync(
            x => x.OrganizationId == message.OrganizationId && x.RoleId == message.RoleId,
            context.CancellationToken);
        if (role is null)
        {
            domainWriteContext.Add(OrganizationRoleDirectoryEntry.Create(
                message.OrganizationId,
                message.RoleId,
                message.Revision,
                message.IsSystem,
                message.Permissions,
                message.UpdatedAt));
            await domainWriteContext.CommitAsync(context.CancellationToken);
            return;
        }

        if (!role.ApplyUpsert(
                message.Revision,
                message.IsSystem,
                message.Permissions,
                message.UpdatedAt))
        {
            return;
        }

        await reconciler.ReconcileRoleAuthorizationAsync(
            role,
            message.UpdatedAt,
            context.CancellationToken);
        await domainWriteContext.CommitAsync(context.CancellationToken);
    }
}
