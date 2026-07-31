using System.Reflection;
using System.Text.RegularExpressions;
using MassTransit;
using MassTransit.Metadata;

namespace Palladin.Core.MassTransit.BuildingBlocks;

internal static class ConsumersBuildingBlock
{
    internal static void Register(
        Assembly[] assemblies,
        IBusRegistrationConfigurator configurator,
        MassTransitOptions options)
    {
        if (options.Consumers.Disabled || assemblies.Length == 0)
        {
            return;
        }

        var types = assemblies
            .SelectMany(a => { try { return a.GetTypes(); } catch { return []; } })
            .Where(RegistrationMetadata.IsConsumerOrDefinition)
            .ToArray();

        configurator.AddConsumers(types);
        configurator.AddSagaStateMachines(assemblies);
        configurator.AddSagas(assemblies);
        configurator.AddActivities(assemblies);
    }

    internal static IRabbitMqBusFactoryConfigurator ConfigureConsumers(
        this IRabbitMqBusFactoryConfigurator cfg,
        IBusRegistrationContext context,
        MassTransitOptions options,
        ICollection<Assembly> assemblies)
    {
        if (options.Consumers.Disabled)
        {
            return cfg;
        }

        cfg.ConfigureEndpoints(
            context,
            x =>
            {
                var matchedTypes = GetTypesMatchingPatterns(assemblies, options.Consumers.Blacklist);
                x.Exclude(matchedTypes);
            }
        );

        return cfg;
    }

    private static Type[]
        GetTypesMatchingPatterns(IEnumerable<Assembly> assemblies, ICollection<string> regexPatterns) =>
        assemblies.SelectMany(assembly => assembly.GetTypes())
            .Where(
                type => !string.IsNullOrEmpty(type.FullName) &&
                        regexPatterns.Any(
                            pattern => Regex.IsMatch(
                                type.FullName,
                                pattern,
                                RegexOptions.Compiled | RegexOptions.IgnoreCase
                            )
                        )
            )
            .ToArray();
}
