using NodaTime;

namespace Palladin.Module.Search.Domain;

// Tenant-first administrative search row. Only server-visible Agent and Member attributes are
// accepted; canonical Vault/Entry presentation never enters this model.
internal sealed class SearchItem
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public string Type { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string SearchText { get; private set; } = string.Empty;
    public Instant UpdatedAt { get; private set; }
    public bool IsRemoved { get; private set; }

    private SearchItem() { }

    internal static SearchItem Create(
        Guid id,
        Guid organizationId,
        string type,
        string name,
        string searchText,
        Instant updatedAt) =>
        new()
        {
            Id = id,
            OrganizationId = organizationId,
            Type = type,
            Name = name,
            SearchText = searchText,
            UpdatedAt = updatedAt,
        };

    internal static SearchItem CreateRemovalTombstone(
        Guid id,
        Guid organizationId,
        string type,
        Instant occurredAt) =>
        new()
        {
            Id = id,
            OrganizationId = organizationId,
            Type = type,
            UpdatedAt = occurredAt,
            IsRemoved = true,
        };

    internal void Apply(
        Guid organizationId,
        string name,
        string searchText,
        Instant updatedAt)
    {
        OrganizationId = organizationId;
        Name = name;
        SearchText = searchText;
        UpdatedAt = updatedAt;
        IsRemoved = false;
    }

    internal void Remove(Instant occurredAt)
    {
        Name = string.Empty;
        SearchText = string.Empty;
        UpdatedAt = occurredAt;
        IsRemoved = true;
    }
}
