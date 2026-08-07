using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Notification.Domain;
using Palladin.Module.Notification.Contracts.Commands;
using Palladin.Module.Notification.Contracts.ValueObjects;
using Palladin.Module.Notification.Infrastructure.MassTransit;
using Palladin.Module.Notification.Infrastructure.Persistence;
using JetBrains.Annotations;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Palladin.Module.Notification.Triggers;

[UsedImplicitly]
internal sealed class OnUserUpsertedDefinition : ConsumerDefinition<OnUserUpserted>
{
    public OnUserUpsertedDefinition() => EndpointName = NotificationEndpoints.FromIdentity;
}

[UsedImplicitly]
internal sealed class OnUserUpserted(
    NotificationDomainWriteContext domainWriteContext) : IConsumer<UserUpsertedEvent>
{
    public async Task Consume(ConsumeContext<UserUpsertedEvent> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;

        var existing = await domainWriteContext.Users.FirstOrDefaultAsync(u => u.UserId == msg.UserId, ct);
        if (existing is null)
        {
            domainWriteContext.Add(User.Create(
                msg.UserId, msg.OrganizationId, msg.DisplayName, msg.Email, msg.Permissions, msg.UpdatedAt));
        }
        else
        {
            if (msg.UpdatedAt > existing.UpdatedAt)
            {
                existing.Update(msg.DisplayName, msg.Email, msg.UpdatedAt);
            }

            if (existing.ApplyPermissions(msg.Permissions))
            {
                await CascadeLostPermissionItemsAsync(msg.OrganizationId, msg.UserId, msg.Permissions, ct);
            }
        }

        await EnsureSelfScopeAsync(msg.OrganizationId, msg.UserId, ct);

        await domainWriteContext.CommitAsync(ct);
    }

    private async Task EnsureSelfScopeAsync(Guid organizationId, Guid userId, CancellationToken ct)
    {
        var exists = await domainWriteContext.Scopes.AnyAsync(
            s => s.OrganizationId == organizationId
                 && s.UserId == userId
                 && s.Type == NotificationScopeTypes.User
                 && s.ItemId == userId,
            ct);
        if (!exists)
        {
            domainWriteContext.Add(Scope.Create(organizationId, userId, NotificationScopeTypes.User, userId));
        }
    }

    private async Task CascadeLostPermissionItemsAsync(
        Guid organizationId, Guid userId, Permission newPermissions, CancellationToken ct)
    {
        var lost = await domainWriteContext.InboxItems
            .Where(i => i.OrganizationId == organizationId
                        && i.UserId == userId
                        && i.RequiredPermission != null
                        && (i.RequiredPermission.Value & newPermissions) == 0)
            .ToListAsync(ct);

        domainWriteContext.RemoveRange(lost);
    }
}
