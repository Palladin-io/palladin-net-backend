namespace Palladin.Module.Identity.Infrastructure.Options;

internal sealed class JwtOptions
{
    public const string Position = "Modules:Identity:Jwt";
    public string Secret { get; init; } = string.Empty;
    public string Issuer { get; init; } = string.Empty;
    public string Audience { get; init; } = string.Empty;
    public int AccessTokenExpiryMinutes { get; init; } = 15;
    public int RefreshTokenExpiryDays { get; init; } = 365;
    public int RefreshTokenRotationThresholdDays { get; init; } = 7;
    public int RefreshTokenConcurrencyRetryLimit { get; init; } = 4;
}
