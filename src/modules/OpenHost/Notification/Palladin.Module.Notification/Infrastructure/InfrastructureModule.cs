using System.Text.Json.Serialization;
using Palladin.Module.Notification.Infrastructure.Email;
using Palladin.Module.Notification.Infrastructure.Email.Events;
using Palladin.Module.Notification.Infrastructure.Email.Suppression;
using Palladin.Module.Notification.Infrastructure.Email.Templating;
using Palladin.Module.Notification.Infrastructure.Persistence;
using Palladin.Module.Notification.Infrastructure.Push;
using Palladin.Module.Notification.Infrastructure.SignalR;
using Amazon.SimpleEmailV2;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;

namespace Palladin.Module.Notification.Infrastructure;

internal static class InfrastructureModule
{
    // Options carry only their leaf section name; the module prefix is composed here at registration.
    private const string ConfigPrefix = "Modules:Notification";

    private static string Section(string leaf) => $"{ConfigPrefix}:{leaf}";

    internal static IServiceCollection AddNotificationInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddNotificationPersistence(configuration);

        services.AddSignalR()
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
                options.PayloadSerializerOptions.Converters.Add(
                    new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
            });
        services.AddScoped<IWebNotifier, WebNotifier>();

        services.Configure<FirebaseOptions>(configuration.GetSection(FirebaseOptions.Position));
        services.Configure<PushTextOptions>(configuration.GetSection(PushTextOptions.Position));
        services.AddSingleton<IFirebaseMessagingProvider, FirebaseMessagingProvider>();
        services.AddSingleton<PushText>();
        services.AddScoped<IPushNotificationService, FirebasePushNotificationService>();

        services.AddOptions<SesOptions>()
            .Bind(configuration.GetSection(Section(SesOptions.Position)))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<SesOptions>, SesOptionsValidator>();
        services.AddSingleton<SesClientProvider>();
        services.AddScoped<IEmailSuppressionStore, EmailSuppressionStore>();
        services.AddScoped<IEmailSender>(sp => new SesEmailSender(
            sp.GetRequiredService<SesClientProvider>().Client,
            sp.GetRequiredService<IEmailSuppressionStore>(),
            sp.GetRequiredService<IOptions<SesOptions>>(),
            sp.GetRequiredService<ILogger<SesEmailSender>>()));
        services.AddScoped<IEmailDispatchDeduplicator, EmailDispatchDeduplicator>();
        services.AddHostedService<SesEventPoller>();

        services.Configure<EmailBrandingOptions>(configuration.GetSection(Section(EmailBrandingOptions.Position)));
        services.AddSingleton<IEmailTemplateRenderer, FluidEmailTemplateRenderer>();

        return services;
    }
}
