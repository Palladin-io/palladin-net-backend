using Palladin.Core.Events;
using Palladin.Module.Vault.Contracts.Events;
using NodaTime;

namespace Palladin.Module.Vault.Domain;

internal sealed class CredentialFailureReport : EventEntityBase
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid VaultId { get; private set; }
    public Guid EntryId { get; private set; }
    public Guid AgentId { get; private set; }

    public CredentialFailureCode Code { get; private set; }
    public Instant CreatedAt { get; private set; }

    private CredentialFailureReport() { }

    internal static CredentialFailureReport Create(
        Guid id,
        Guid organizationId,
        Guid vaultId,
        Guid entryId,
        Guid agentId,
        CredentialFailureCode code,
        Instant now)
    {
        var report = new CredentialFailureReport
        {
            Id = id,
            OrganizationId = organizationId,
            VaultId = vaultId,
            EntryId = entryId,
            AgentId = agentId,
            Code = code,
            CreatedAt = now,
        };

        report.AddEvent(new CredentialFailureReportedEvent(
            id, organizationId, vaultId, entryId, agentId,
            CodeWire(code), now));

        return report;
    }

    internal static string CodeWire(CredentialFailureCode code) => code switch
    {
        CredentialFailureCode.LoginRejected => "login_rejected",
        CredentialFailureCode.AuthFailed => "auth_failed",
        _ => "manual",
    };
}
