using Palladin.Module.Notification.Domain;
using Microsoft.EntityFrameworkCore;

namespace Palladin.Module.Notification.Infrastructure.Persistence;

internal sealed class NotificationDbWriteContext(DbContextOptions<NotificationDbWriteContext> options) : DbContext(options)
{
    public DbSet<PushToken> PushTokens => Set<PushToken>();
    public DbSet<InboxItem> InboxItems => Set<InboxItem>();
    public DbSet<Scope> Scopes => Set<Scope>();
    public DbSet<User> Users => Set<User>();
    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();
    public DbSet<SuppressedEmail> SuppressedEmails => Set<SuppressedEmail>();
    public DbSet<EmailDelivery> EmailDeliveries => Set<EmailDelivery>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(NotificationDbWriteContext).Assembly);
    }
}
