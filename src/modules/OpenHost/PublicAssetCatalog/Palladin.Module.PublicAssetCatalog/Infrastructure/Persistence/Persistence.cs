using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Palladin.Core.Events;
using Palladin.Core.Persistence;
using Palladin.Core.Persistence.Hooks;
using Palladin.Module.PublicAssetCatalog.Domain;

namespace Palladin.Module.PublicAssetCatalog.Infrastructure.Persistence;

internal sealed class PublicAssetCatalogPersistenceOptions : IPersistenceOptions
{
    public const string Position = "Modules:PublicAssetCatalog:Persistence";
    public string ConnectionString { get; init; } = string.Empty;
    public bool AutoMigration { get; init; }
}
internal sealed class PublicAssetCatalogDbWriteContext(DbContextOptions<PublicAssetCatalogDbWriteContext> options) : DbContext(options)
{
    public DbSet<PublicAsset> Assets => Set<PublicAsset>();
    public DbSet<PublicAssetUploadSession> UploadSessions => Set<PublicAssetUploadSession>();
    protected override void OnModelCreating(ModelBuilder b) => b.ApplyConfigurationsFromAssembly(typeof(PublicAssetCatalogDbWriteContext).Assembly);
}
internal sealed class PublicAssetCatalogDbReadContext(DbContextOptions<PublicAssetCatalogDbReadContext> options) : DbContext(options)
{
    public DbSet<PublicAsset> Assets => Set<PublicAsset>();
    public DbSet<PublicAssetUploadSession> UploadSessions => Set<PublicAssetUploadSession>();
    protected override void OnModelCreating(ModelBuilder b) => b.ApplyConfigurationsFromAssembly(typeof(PublicAssetCatalogDbReadContext).Assembly);
    public override int SaveChanges() => throw new ReadOnlyContextSaveChangesException(nameof(PublicAssetCatalogDbReadContext));
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => throw new ReadOnlyContextSaveChangesException(nameof(PublicAssetCatalogDbReadContext));
}
internal sealed class PublicAssetCatalogDomainReadContext(PublicAssetCatalogDbReadContext context) : DomainReadContextBase(context)
{
    public IQueryable<PublicAsset> Assets => Query<PublicAsset>();
    public IQueryable<PublicAssetUploadSession> UploadSessions => Query<PublicAssetUploadSession>();
}
internal sealed class PublicAssetCatalogDomainWriteContext(PublicAssetCatalogDbWriteContext context, IEnumerable<IEventPublisher> publishers) : DomainWriteContextBase(context, publishers)
{
    public IQueryable<PublicAsset> Assets => Track<PublicAsset>();
    public IQueryable<PublicAssetUploadSession> UploadSessions => Track<PublicAssetUploadSession>();
    public void Add(PublicAsset asset) => base.Add(asset);
    public void Add(PublicAssetUploadSession session) => base.Add(session);
    public Task<List<PublicAsset>> LockAssetsForAcquisitionDispatchAsync(Guid[] assetIds, CancellationToken ct) =>
        context.Assets
            .FromSqlInterpolated($"SELECT * FROM \"Assets\" WHERE \"Id\" = ANY({assetIds}) ORDER BY \"Id\" FOR UPDATE")
            .ToListAsync(ct);
}
internal static class PersistenceModule
{
    internal static IServiceCollection AddPublicAssetCatalogPersistence(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<PublicAssetCatalogPersistenceOptions>(config.GetSection(PublicAssetCatalogPersistenceOptions.Position));
        services.AddDbContext<PublicAssetCatalogDbWriteContext>((sp, o) => o.UseNpgsql(sp.GetRequiredService<IOptions<PublicAssetCatalogPersistenceOptions>>().Value.ConnectionString, n => n.UseNodaTime()));
        services.AddDbContext<PublicAssetCatalogDbReadContext>((sp, o) => o.UseNpgsql(sp.GetRequiredService<IOptions<PublicAssetCatalogPersistenceOptions>>().Value.ConnectionString, n => n.UseNodaTime()).UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking));
        services.AddScoped<PublicAssetCatalogDomainReadContext>(); services.AddScoped<PublicAssetCatalogDomainWriteContext>();
        services.AddDbMigrationHook<PublicAssetCatalogDbWriteContext, PublicAssetCatalogPersistenceOptions>();
        return services;
    }
}
