using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.Consents;
using Palladin.Module.Vault.Infrastructure.Purge;
using Palladin.Module.Vault.Infrastructure.Assets;
using Palladin.Module.Audit.Infrastructure.Exports;
using Palladin.Module.Agents.Infrastructure.PublicAssets;
using Palladin.Core.Guid;
using Palladin.Module.Identity.Contracts.ValueObjects;
using Palladin.Module.Identity.Domain.Enums;
using Palladin.Module.Identity.Infrastructure.OAuth;
using Palladin.Module.Notification.Infrastructure.Push;
using Palladin.Tests.Integrations.Shared.Mocks;
using Hangfire;
using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Serilog;

namespace Palladin.Tests.Integrations.Shared;

[UsedImplicitly]
public class ApiFactory : AppFixture<Palladin.Api.Program>
{
    public FakeClock FakeClock { get; } = new(SystemClock.Instance.GetCurrentInstant());
    public IGuidProvider GuidProvider { get; } = Substitute.For<IGuidProvider>();
    public ILogger Logger { get; } = Substitute.For<ILogger>();
    internal IExternalOAuthProvider GoogleOAuthProvider { get; } = Substitute.For<IExternalOAuthProvider>();
    internal IPushNotificationService PushNotificationService { get; } = Substitute.For<IPushNotificationService>();
    public IBackgroundJobClient BackgroundJobClient { get; } = Substitute.For<IBackgroundJobClient>();
    internal IEntryPurgeLedger EntryPurgeLedger { get; } = Substitute.For<IEntryPurgeLedger>();
    internal IEntryAssetPurger EntryAssetPurger { get; } = Substitute.For<IEntryAssetPurger>();
    internal InMemoryCdnService CdnService { get; } = new();
    internal IPublicAssetCatalogClient PublicAssetCatalogClient { get; } = Substitute.For<IPublicAssetCatalogClient>();

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddMassTransitTestHarness();
        services.Replace(ServiceDescriptor.Singleton(new ConsentNoticeCatalog([
            new ConsentNotice(ConsentPurpose.ProductAnalytics, ConsentPurpose.Scope(ConsentPurpose.ProductAnalytics), "2026-09-10T00:00:00Z", "en"),
            new ConsentNotice(ConsentPurpose.EmailMarketing, ConsentPurpose.Scope(ConsentPurpose.EmailMarketing), "2026-09-10T00:00:00Z", "en"),
        ])));
        services.Replace(ServiceDescriptor.Singleton<IClock>(FakeClock));
        services.Replace(ServiceDescriptor.Singleton(GuidProvider));
        services.Replace(ServiceDescriptor.Singleton(Logger));
        services.RemoveAll<IExternalOAuthProvider>();
        services.AddSingleton(GoogleOAuthProvider);
        services.RemoveAll<IPushNotificationService>();
        services.AddScoped(_ => PushNotificationService);
        services.Replace(ServiceDescriptor.Singleton(BackgroundJobClient));
        services.Replace(ServiceDescriptor.Singleton(EntryPurgeLedger));
        services.Replace(ServiceDescriptor.Singleton(EntryAssetPurger));
        services.Replace(ServiceDescriptor.Singleton<IEncryptedPresentationAssetStorage>(CdnService));
        services.Replace(ServiceDescriptor.Singleton<ILegacyPresentationAssetStorage>(CdnService));
        services.Replace(ServiceDescriptor.Singleton<IAuditExportStorage>(CdnService));
        services.RemoveAll<IPublicAssetCatalogClient>();
        services.AddSingleton(PublicAssetCatalogClient);
    }

    protected override void ConfigureApp(IWebHostBuilder a)
    {
        base.ConfigureApp(a);

        SetupDefaultMocks();
    }

    private void SetupDefaultMocks()
    {
        this.MockId(Guid.NewGuid());
        GoogleOAuthProvider.Provider.Returns(AuthProvider.Google);
    }
}

[UsedImplicitly]
public sealed class ApiFactoryCollection : TestCollection<ApiFactory>;
