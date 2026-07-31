using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Notification.Domain;
using Palladin.Module.Notification.Contracts.Commands;
using Palladin.Module.Notification.Contracts.ValueObjects;
using Palladin.Module.Notification.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Palladin.Tests.Integrations.Shared.Seeders;

internal static class NotificationSeeder
{
    public static async Task SeedPushTokenAsync(
        this IServiceProvider serviceProvider,
        Guid userId,
        Guid organizationId,
        PushPlatform platform)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<NotificationDbWriteContext>();

        writeContext.PushTokens.Add(PushToken.Create(
            Guid.NewGuid(), userId, organizationId, $"token-{Guid.NewGuid():N}", platform,
            null, NodaTime.SystemClock.Instance.GetCurrentInstant()));
        await writeContext.SaveChangesAsync();
    }


    public static async Task SeedScopeAsync(
        this IServiceProvider serviceProvider,
        Guid organizationId,
        Guid userId,
        string type,
        Guid itemId)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<NotificationDbWriteContext>();

        await writeContext.Scopes
            .Where(s => s.OrganizationId == organizationId && s.UserId == userId
                        && s.Type == type && s.ItemId == itemId)
            .ExecuteDeleteAsync();

        writeContext.Scopes.Add(Scope.Create(organizationId, userId, type, itemId));
        await writeContext.SaveChangesAsync();
    }

    public static Task SeedVaultScopeAsync(
        this IServiceProvider serviceProvider,
        Guid organizationId,
        Guid vaultId,
        Guid userId) =>
        serviceProvider.SeedScopeAsync(organizationId, userId, NotificationScopeTypes.Vault, vaultId);

    public static Task SeedUserScopeAsync(
        this IServiceProvider serviceProvider,
        Guid organizationId,
        Guid userId) =>
        serviceProvider.SeedScopeAsync(organizationId, userId, NotificationScopeTypes.User, userId);

    public static async Task SeedNotificationUserAsync(
        this IServiceProvider serviceProvider,
        Guid organizationId,
        Guid userId,
        Permission permissions)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<NotificationDbWriteContext>();

        await writeContext.Users.Where(u => u.UserId == userId).ExecuteDeleteAsync();

        writeContext.Users.Add(User.Create(
            userId, organizationId, "Test User", $"{userId:N}@test.io", permissions,
            SystemClock.Instance.GetCurrentInstant()));
        await writeContext.SaveChangesAsync();
    }

    public static async Task SeedPreferenceAsync(
        this IServiceProvider serviceProvider,
        Guid organizationId,
        Guid userId,
        Core.Types.NotificationType type,
        bool inboxEnabled,
        bool signalREnabled,
        bool pushEnabled)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<NotificationDbWriteContext>();

        await writeContext.NotificationPreferences
            .Where(p => p.OrganizationId == organizationId && p.UserId == userId && p.Type == type)
            .ExecuteDeleteAsync();

        writeContext.NotificationPreferences.Add(NotificationPreference.Create(
            organizationId, userId, type, inboxEnabled, signalREnabled, pushEnabled,
            SystemClock.Instance.GetCurrentInstant()));
        await writeContext.SaveChangesAsync();
    }
}
