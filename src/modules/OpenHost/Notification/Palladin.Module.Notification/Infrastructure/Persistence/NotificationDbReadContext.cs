using Palladin.Core.Persistence;
using Palladin.Module.Notification.Domain;
using Microsoft.EntityFrameworkCore;

namespace Palladin.Module.Notification.Infrastructure.Persistence;

internal sealed class NotificationDbReadContext(DbContextOptions<NotificationDbReadContext> options) : DbContext(options)
{
    public DbSet<PushToken> PushTokens => Set<PushToken>();
    public DbSet<InboxItem> InboxItems => Set<InboxItem>();
    public DbSet<Scope> Scopes => Set<Scope>();
    public DbSet<User> Users => Set<User>();
    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();
    public DbSet<SuppressedEmail> SuppressedEmails => Set<SuppressedEmail>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(NotificationDbWriteContext).Assembly);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        throw new ReadOnlyContextSaveChangesException(nameof(NotificationDbReadContext));
}
