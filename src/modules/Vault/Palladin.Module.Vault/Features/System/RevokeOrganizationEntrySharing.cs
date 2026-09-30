using JetBrains.Annotations;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using Palladin.Module.Vault.Contracts.Commands;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.MassTransit;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Sharing;

namespace Palladin.Module.Vault.Features;

[UsedImplicitly]
internal sealed class RevokeOrganizationEntrySharingConsumerDefinition
    : ConsumerDefinition<RevokeOrganizationEntrySharingConsumer>
{
    public RevokeOrganizationEntrySharingConsumerDefinition() => EndpointName = VaultEndpoints.Sharing;
}

[PublicAPI]
internal sealed class RevokeOrganizationEntrySharingConsumer(
    VaultDomainWriteContext domainWriteContext, IOptions<EntrySharingOptions> options, IClock clock)
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
        await EntryShareSourceRevocation.RevokeAsync(domainWriteContext,
            domainWriteContext.EntryShares.Where(x => x.OrganizationId == msg.OrganizationId),
            options.Value.SourceRevocationBatchSize, clock.GetCurrentInstant(), context.CancellationToken);
        await context.RespondAsync(new OrganizationEntrySharingRevoked(msg.OrganizationId));
    }
}
