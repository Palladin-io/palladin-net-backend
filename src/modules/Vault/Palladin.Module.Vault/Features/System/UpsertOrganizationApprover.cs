using JetBrains.Annotations;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Palladin.Module.Vault.Contracts.Commands;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.MassTransit;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Features;

[UsedImplicitly]
internal sealed class UpsertOrganizationApproverConsumerDefinition
    : ConsumerDefinition<UpsertOrganizationApproverConsumer>
{
    public UpsertOrganizationApproverConsumerDefinition() => EndpointName = VaultEndpoints.Approvers;
}

[UsedImplicitly]
internal sealed class UpsertOrganizationApproverConsumer(VaultDomainWriteContext domainWriteContext)
    : IConsumer<UpsertOrganizationApproverCommand>
{
    public async Task Consume(ConsumeContext<UpsertOrganizationApproverCommand> context)
    {
        var msg = context.Message;
        var existing = await domainWriteContext.OrganizationApprovers.SingleOrDefaultAsync(
            x => x.OrganizationId == msg.OrganizationId && x.UserId == msg.UserId,
            context.CancellationToken);
        if (existing is null)
        {
            domainWriteContext.Add(OrganizationApprover.Create(
                msg.OrganizationId, msg.UserId, msg.IsApprover, msg.UpdatedAt));
        }
        else if (!existing.Apply(msg.IsApprover, msg.UpdatedAt))
        {
            return;
        }

        await domainWriteContext.CommitAsync(context.CancellationToken);
    }
}
