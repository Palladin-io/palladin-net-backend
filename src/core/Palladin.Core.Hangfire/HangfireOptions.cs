namespace Palladin.Core.Hangfire;

[UsedImplicitly]
internal sealed class HangfireOptions
{
    public const string Position = "Hangfire";

    public bool Enabled { get; set; } = true;
    public string Url { get; init; } = "/hangfire";
    public string ConnectionString { get; init; } = null!;
    public string DashboardLogin { get; init; } = null!;
    public string DashboardPassword { get; init; } = null!;
    public string TimeZoneId { get; init; } = "Europe/Warsaw";
}
