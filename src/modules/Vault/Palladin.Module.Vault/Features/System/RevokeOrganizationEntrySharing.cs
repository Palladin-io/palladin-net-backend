using JetBrains.Annotations;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Palladin.Module.Vault.Contracts.Commands;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.MassTransit;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Features;

[UsedImplicitly]
internal sealed class RevokeOrganizationEntrySharingConsumerDefinition
    : ConsumerDefinition<RevokeOrganizationEntrySharingConsumer>
{
    public RevokeOrganizationEntrySharingConsumerDefinition() => EndpointName = VaultEndpoints.Sharing;
}

[PublicAPI]
internal sealed class RevokeOrganizationEntrySharingConsumer(VaultDomainWriteContext domainWriteContext)
    : IConsumer<RevokeOrganizationEntrySharingCommand>
{
    public async Task Consume(ConsumeContext<RevokeOrganizationEntrySharingCommand> context)
    {
        var msg = context.Message;
        var authority = await domainWriteContext.VaultOrganizationLifecycles.SingleOrDefaultAsync(
            x => x.OrganizationId == msg.OrganizationId, context.CancellationToken);
        if (authority is null)
        {
            authority = VaultOrganizationLifecycle.Create(msg.OrganizationId);
            domainWriteContext.Add(authority);
        }

        authority.DisableSharing();
        await domainWriteContext.CommitAsync(context.CancellationToken);
        await context.RespondAsync(new OrganizationEntrySharingRevoked(msg.OrganizationId));
    }
}
