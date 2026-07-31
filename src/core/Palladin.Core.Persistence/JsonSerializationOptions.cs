using System.Text.Json;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;

namespace Palladin.Core.Persistence;

public static class PalladinPersistenceJsonOptions
{
    public static readonly JsonSerializerOptions Instance;

    static PalladinPersistenceJsonOptions()
    {
        Instance = new JsonSerializerOptions()
            .ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
    }
}
