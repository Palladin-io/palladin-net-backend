using System.Text.Json;
using Palladin.Core.Json;
using MassTransit;

namespace Palladin.Core.MassTransit.BuildingBlocks;

internal static class JsonSerializerBuildingBlock
{
    internal static void Configure(
        IRabbitMqBusFactoryConfigurator cfg,
        Func<JsonSerializerOptions, JsonSerializerOptions>? configure
    )
    {
        JsonSerializerOptions DefaultJsonConfigure(JsonSerializerOptions jsonOptions)
        {
            jsonOptions.AddPalladinDefaultConfiguration();

            configure?.Invoke(jsonOptions);

            return jsonOptions;
        }

        cfg.ConfigureJsonSerializerOptions(DefaultJsonConfigure);
    }
}
