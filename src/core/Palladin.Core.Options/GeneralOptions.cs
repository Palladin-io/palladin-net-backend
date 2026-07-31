namespace Palladin.Core.Options;

public sealed class GeneralOptions
{
    public const string Position = "General";

    public IDictionary<string, TenantOption> Tenants { get; init; } = new Dictionary<string, TenantOption>();
}

public sealed class TenantOption
{
    public string Url { get; init; }
}
