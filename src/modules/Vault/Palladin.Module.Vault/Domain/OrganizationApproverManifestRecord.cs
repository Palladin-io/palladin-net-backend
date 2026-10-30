using Palladin.Core.Types.Exceptions;

namespace Palladin.Module.Vault.Domain;

// Append-only signed approver manifest, stored as its exact canonical JSON document.
internal sealed class OrganizationApproverManifestRecord
{
    public Guid OrganizationId { get; private set; }
    public ulong ManifestRevision { get; private set; }
    public uint KeyVersion { get; private set; }
    public string Document { get; private set; } = string.Empty;

    private OrganizationApproverManifestRecord() { }

    internal static OrganizationApproverManifestRecord Create(
        Guid organizationId,
        ulong manifestRevision,
        uint keyVersion,
        string document)
    {
        if (organizationId == Guid.Empty || manifestRevision == 0 || keyVersion == 0
            || string.IsNullOrEmpty(document))
        {
            throw new DomainException("Approver manifest record is invalid.");
        }

        return new OrganizationApproverManifestRecord
        {
            OrganizationId = organizationId,
            ManifestRevision = manifestRevision,
            KeyVersion = keyVersion,
            Document = document,
        };
    }
}
