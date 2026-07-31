using System.Text.Json;
using System.Text.Json.Serialization;
using NodaTime.Serialization.SystemTextJson;

namespace Palladin.Core.Json;

public static class PalladinJsonSerializationSettings
{
    public static JsonSerializerOptions? DefaultOptions { get; }

    public static IList<JsonConverter> Converters { get; }

    static PalladinJsonSerializationSettings()
    {
        Converters = new List<JsonConverter>
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        };

        DefaultOptions = AddPalladinDefaultConfiguration(new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    public static JsonSerializerOptions AddPalladinDefaultConfiguration(this JsonSerializerOptions baseOptions)
    {
        foreach (var converter in Converters)
        {
            baseOptions.Converters.Add(converter);
        }

        baseOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;

        // NodaTime must be configured HERE (not only on DefaultOptions) so the MassTransit bus
        // serializer — which calls AddPalladinDefaultConfiguration — round-trips Instant correctly.
        // Without it, every integration-event Instant (UpdatedAt, OccurredAt, ...) deserializes to
        // epoch over the bus, breaking UpdatedAt-based idempotency (replica/audit/grant consumers).
        return baseOptions.ConfigureForNodaTime(new NodaJsonSettings());
    }
}
