using NodaTime;

namespace Palladin.Module.Agents.Domain;

internal enum FormDiscoveryMapStatus
{
    Candidate = 0,
    Observed = 1,
    Verified = 2,
}

internal sealed class FormDiscoveryMap
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid? SubmittedByAgentId { get; private set; }
    public string Domain { get; private set; } = string.Empty;
    public string LoginUrl { get; private set; } = string.Empty;
    public string Provider { get; private set; } = string.Empty;
    public string Fingerprint { get; private set; } = string.Empty;
    public int MapVersion { get; private set; }
    public string DefinitionJson { get; private set; } = string.Empty;
    public FormDiscoveryMapStatus Status { get; private set; }
    public Instant CreatedAt { get; private set; }
    public Instant UpdatedAt { get; private set; }

    private FormDiscoveryMap() { }

    internal static FormDiscoveryMap CreateCandidate(
        Guid id, Guid organizationId, Guid agentId, string domain, string loginUrl,
        string provider, string fingerprint, string definitionJson, int mapVersion, Instant now) => new()
    {
        Id = id,
        OrganizationId = organizationId,
        SubmittedByAgentId = agentId,
        Domain = domain,
        LoginUrl = loginUrl,
        Provider = provider,
        Fingerprint = fingerprint,
        MapVersion = mapVersion,
        DefinitionJson = definitionJson,
        Status = FormDiscoveryMapStatus.Candidate,
        CreatedAt = now,
        UpdatedAt = now,
    };

    internal void MarkVerified(Instant now)
    {
        Status = FormDiscoveryMapStatus.Verified;
        UpdatedAt = now;
    }
}
