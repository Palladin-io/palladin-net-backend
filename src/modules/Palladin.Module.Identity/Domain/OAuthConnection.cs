using Palladin.Module.Identity.Contracts.ValueObjects;
using Palladin.Module.Identity.Domain.Enums;
using NodaTime;

namespace Palladin.Module.Identity.Domain;

internal sealed class OAuthConnection
{
    public Guid UserId { get; private set; }
    public Guid Id { get; private set; }
    public AuthProvider Provider { get; private set; }
    public string ProviderUserId { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;
    public Instant CreatedAt { get; private set; }

    public User User { get; private set; } = null!;

    private OAuthConnection() { }

    internal static OAuthConnection Create(
        Guid id,
        Guid userId,
        AuthProvider provider,
        string providerUserId,
        string email,
        Instant now) =>
        new()
        {
            Id = id,
            UserId = userId,
            Provider = provider,
            ProviderUserId = providerUserId,
            Email = email,
            CreatedAt = now,
        };
}
