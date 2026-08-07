using NodaTime;

namespace Palladin.Module.Vault.Domain;

// Read-model replica of an Identity user, owned by the Vault module and kept up to date via
// UserUpsertedEvent from Identity (queue vault.events.identity). Lets Vault resolve actor names
// (who approved / revoked / denied a grant) without a synchronous cross-module call.
internal sealed class User
{
    public Guid Id { get; private set; }
    public string DisplayName { get; private set; } = string.Empty;
    public Instant CreatedAt { get; private set; }
    public Instant UpdatedAt { get; private set; }

    private User() { }

    internal static User Create(Guid id, string displayName, Instant createdAt, Instant updatedAt) =>
        new() { Id = id, DisplayName = displayName, CreatedAt = createdAt, UpdatedAt = updatedAt };

    internal void UpdateDisplayName(string displayName, Instant updatedAt)
    {
        DisplayName = displayName;
        UpdatedAt = updatedAt;
    }
}
