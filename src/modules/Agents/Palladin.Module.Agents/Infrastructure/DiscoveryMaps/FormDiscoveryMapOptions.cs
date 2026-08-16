namespace Palladin.Module.Agents.Infrastructure.DiscoveryMaps;

internal sealed class FormDiscoveryMapOptions
{
    public const string Position = "Modules:Agents:FormDiscoveryMaps";

    public int MaximumDefinitionBytes { get; init; } = 65_536;
    public int MaximumLoginUrlBytes { get; init; } = 2_048;
    public int MaximumProviderLength { get; init; } = 64;
    public int MaximumJsonDepth { get; init; } = 32;
    public int MaximumSteps { get; init; } = 8;
    public int MaximumFields { get; init; } = 16;
    public int MaximumFieldIdLength { get; init; } = 128;
    public int MaximumSelectorBytes { get; init; } = 1_024;
    public int MaximumCookieOverlays { get; init; } = 4;
    public int MaximumSelectorsPerOverlay { get; init; } = 8;
    public int MinimumWaitTimeoutMilliseconds { get; init; } = 100;
    public int MaximumWaitTimeoutMilliseconds { get; init; } = 60_000;
    public int MaximumLookupRevisions { get; init; } = 20;
}
