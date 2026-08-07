using NodaTime;

namespace Palladin.Module.Agents.Domain;

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
