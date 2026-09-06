namespace Palladin.Module.Agents.Domain;

internal sealed class AgentDisplayNameFence
{
    public Guid OrganizationId { get; private set; }
    public uint Revision { get; private set; }

    private AgentDisplayNameFence() { }

    internal static AgentDisplayNameFence Create(Guid organizationId) =>
        new() { OrganizationId = organizationId, Revision = 1 };

    internal void Advance() => Revision = checked(Revision + 1);
}
