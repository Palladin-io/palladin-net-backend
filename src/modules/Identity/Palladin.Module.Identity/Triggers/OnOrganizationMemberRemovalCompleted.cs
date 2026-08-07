using JetBrains.Annotations;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.MassTransit;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Vault.Contracts.Commands;
using Palladin.Module.Vault.Contracts.Events;

namespace Palladin.Module.Identity.Triggers;

[UsedImplicitly]
internal sealed class OnOrganizationMemberRemovalCompletedDefinition
    : ConsumerDefinition<OnOrganizationMemberRemovalCompleted>
{
    public OnOrganizationMemberRemovalCompletedDefinition() => EndpointName = IdentityEndpoints.Self;
}

[UsedImplicitly]
internal sealed class OnOrganizationMemberRemovalCompleted(
    IdentityDomainWriteContext domainWriteContext)
    : IConsumer<OrganizationMemberRemovalCompletedEvent>
{
    private const int TokenPageSize = 100;

    public async Task Consume(ConsumeContext<OrganizationMemberRemovalCompletedEvent> context)
    {
        var message = context.Message;
        var ct = context.CancellationToken;
        await using var transaction = await domainWriteContext.BeginTransactionAsync(ct);
        var member = await domainWriteContext.OrganizationMembers
            .Include(x => x.User)
            .SingleOrDefaultAsync(x => x.OrganizationId == message.OrganizationId
                                       && x.UserId == message.UserId,
                ct);
        if (member is null)
        {
            return;
        }

        var matchesActiveRequest = member.Status == OrganizationMemberStatus.Removing
                                   && member.RemovalRequestId == message.RequestId;
        if (!matchesActiveRequest && message.UpdatedAt <= member.UpdatedAt)
        {
            return;
        }

        if (!matchesActiveRequest)
        {
            throw new InvalidOperationException("Member removal completion does not match the active request.");
        }

        Guid? lastTokenId = null;
        while (true)
        {
            var query = domainWriteContext.RefreshTokens
                .Where(x => x.OrganizationId == message.OrganizationId
                            && x.UserId == message.UserId
                            && x.RevokedAt == null
                            && x.ExpiresAt > message.CompletedAt);
            if (lastTokenId is not null)
            {
                query = query.Where(x => x.Id.CompareTo(lastTokenId.Value) > 0);
            }

            var tokens = await query.OrderBy(x => x.Id).Take(TokenPageSize).ToListAsync(ct);
            if (tokens.Count == 0)
            {
                break;
            }

            foreach (var token in tokens)
            {
                token.Revoke(message.CompletedAt);
            }

            lastTokenId = tokens[^1].Id;
            await domainWriteContext.CommitAsync(ct);
            domainWriteContext.Clear();
            if (tokens.Count < TokenPageSize)
            {
                break;
            }
        }

        member = await domainWriteContext.OrganizationMembers
            .Include(x => x.User)
            .SingleAsync(x => x.OrganizationId == message.OrganizationId && x.UserId == message.UserId, ct);
        var removedByName = await domainWriteContext.Users
            .Where(x => x.Id == member.RemovalRequestedBy)
            .Select(x => x.DisplayName)
            .SingleAsync(ct);
        member.CompleteRemoval(member.User.DisplayName, removedByName, message.CompletedAt);
        domainWriteContext.Remove(member);
        await domainWriteContext.CommitAsync(transaction, ct);
    }
}
