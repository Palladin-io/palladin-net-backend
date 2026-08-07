namespace Palladin.Module.Notification.Domain;

internal sealed class Scope
{
    public Guid OrganizationId { get; private set; }
    public Guid UserId { get; private set; }
    public string Type { get; private set; } = string.Empty;
    public Guid ItemId { get; private set; }

    private Scope() { }

    internal static Scope Create(Guid organizationId, Guid userId, string type, Guid itemId) =>
        new()
        {
            OrganizationId = organizationId,
            UserId = userId,
            Type = type,
            ItemId = itemId,
        };
}
