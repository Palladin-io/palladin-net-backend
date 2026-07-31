using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using Palladin.Core.Events;
using Palladin.Core.MassTransit.BuildingBlocks;
using Palladin.Core.MassTransit.Events;
using Palladin.Core.MassTransit.Exceptions;
using Palladin.Core.MassTransit.Plugins;
using Palladin.Core.MassTransit.Validation;
using JetBrains.Annotations;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Palladin.Core.MassTransit;

[PublicAPI]
public static class MassTransitModule
{
    private static readonly object _assembliesLock = new();
    private static ImmutableList<Assembly> _assemblies = ImmutableList<Assembly>.Empty;

    public static IServiceCollection AddMassTransitAssembly(
        this IServiceCollection serviceCollection,
        Assembly assembly
    )
    {
        lock (_assembliesLock)
        {
            _assemblies = _assemblies.Add(assembly);
        }

        return serviceCollection;
    }

    public static IServiceCollection AddMassTransitModule(
        this IServiceCollection serviceCollection,
        IConfiguration configuration,
        Func<JsonSerializerOptions, JsonSerializerOptions>? configure = null
    ) =>
        AddMassTransitModule(serviceCollection, configuration, new List<IMassTransitPlugin>(), configure);

    public static IServiceCollection AddMassTransitModule(
        this IServiceCollection serviceCollection,
        IConfiguration configuration,
        ICollection<IMassTransitPlugin> plugins,
        Func<JsonSerializerOptions, JsonSerializerOptions>? configure = null
    )
    {
        var section = configuration.GetSection(MassTransitOptions.Position);

        return serviceCollection
            .AddScoped<IEventPublisher, IntegrationEventPublisher>()
            .Configure<MassTransitOptions>(section)
            .AddMassTransit(
                configurator =>
                {
                    var options = section.Get<MassTransitOptions>() ?? throw new MissingMassTransitConfigurationException();
                    options.Validate();

                    if (options.EnableDelayedMessageScheduler)
                    {
                        configurator.AddDelayedMessageScheduler();
                    }

                    configurator.SetEndpointNameFormatter(new KebabCaseWithNamespacesEndpointNameFormatter());

                    ConsumersBuildingBlock.Register(_assemblies.ToArray(), configurator, options);

                    configurator.UsingRabbitMq(
                        ConfigureRabbitMqMessageBroker(serviceCollection, configuration, options, plugins, configure)
                    );

                    QueueTypeBuildingBlock.Register(configurator, options);

                    foreach (var plugin in plugins)
                    {
                        plugin.RegisterPlugin(configurator, configuration);
                    }
                }
            );
    }

    private static Action<IBusRegistrationContext, IRabbitMqBusFactoryConfigurator> ConfigureRabbitMqMessageBroker(
        IServiceCollection serviceCollection,
        IConfiguration configuration,
        MassTransitOptions options,
        ICollection<IMassTransitPlugin> plugins,
        Func<JsonSerializerOptions, JsonSerializerOptions>? configure
    ) =>
        (context, cfg) =>
        {
            cfg.Host(
                options.RabbitMq.Host,
                options.RabbitMq.Port,
                options.RabbitMq.VirtualHost,
                rabbitMqHostConfigure => rabbitMqHostConfigure.ApplyOptions(options.RabbitMq)
            );

            JsonSerializerBuildingBlock.Configure(cfg, configure);

            cfg.ConfigureKillSwitch(options)
                .ConfigureCircuitBreaker(options)
                .ConfigureRateLimiter(options)
                .ConfigureRetries(options)
                .ConfigureMessageLifetimeScope(context, options)
                .ConfigureInMemoryOutbox(context, options)
                .ConfigureFilters(context)
                .ConfigureConsumers(context, options, _assemblies)
                .ConfigureDelayedMessageScheduler(options);

            foreach (var plugin in plugins)
            {
                plugin.ConfigurePlugin(context, cfg, options);
            }
        };

    public static void UnloadModules()
    {
        OutboxBuildingBlock.OutboxModuleWasLoaded = false;
    }
}
