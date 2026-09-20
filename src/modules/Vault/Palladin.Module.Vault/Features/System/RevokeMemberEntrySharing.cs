using JetBrains.Annotations;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Palladin.Module.Vault.Contracts.Commands;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.MassTransit;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Features;

[UsedImplicitly]
internal sealed class RevokeMemberEntrySharingConsumerDefinition : ConsumerDefinition<RevokeMemberEntrySharingConsumer>
{
    public RevokeMemberEntrySharingConsumerDefinition() => EndpointName = VaultEndpoints.Sharing;
}

[PublicAPI]
internal sealed class RevokeMemberEntrySharingConsumer(VaultDomainWriteContext domainWriteContext)
    : IConsumer<RevokeMemberEntrySharingCommand>
{
    public async Task Consume(ConsumeContext<RevokeMemberEntrySharingCommand> context)
    {
        var msg = context.Message;
        var authority = await domainWriteContext.EntryShareSenderAuthorities.SingleOrDefaultAsync(
            x => x.OrganizationId == msg.OrganizationId && x.UserId == msg.UserId, context.CancellationToken);
        if (authority is null)
        {
            authority = EntryShareSenderAuthority.Create(msg.OrganizationId, msg.UserId);
            domainWriteContext.Add(authority);
        }

        authority.RevokeThrough(msg.AuthorizationVersion);
        await domainWriteContext.CommitAsync(context.CancellationToken);
        await context.RespondAsync(new MemberEntrySharingRevoked(
            msg.OrganizationId, msg.UserId, authority.RevokedThroughAuthorizationVersion));
    }
}
