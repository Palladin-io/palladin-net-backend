namespace Palladin.Module.Identity.Infrastructure.Options;

internal sealed class GoogleOAuthOptions
{
    public const string Position = "Modules:Identity:Google";
    public string ClientId { get; init; } = string.Empty;
}
