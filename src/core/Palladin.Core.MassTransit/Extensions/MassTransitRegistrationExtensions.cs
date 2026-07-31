using System.Reflection;
using MassTransit;
using MassTransit.Metadata;

namespace Palladin.Core.MassTransit.Extensions;

public static class MassTransitRegistrationExtensions
{
    public static void AddConsumersFromAssemblyContaining(this IBusRegistrationConfigurator configurator, Assembly assembly)
    {
        var types = assembly.GetTypes().Where(RegistrationMetadata.IsConsumerOrDefinition);
        configurator.AddConsumers(types.ToArray());
    }
}
