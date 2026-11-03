using NodaTime;
using Palladin.Core.Types.Exceptions;

namespace Palladin.Module.Vault.Domain;

// Identity-fed replica of approver eligibility: an Active organization membership whose effective
// permissions include GrantManage. Combined with MemberKeyDirectory it is the authoritative approver set.
internal sealed class OrganizationApprover
{
    public Guid OrganizationId { get; private set; }
    public Guid UserId { get; private set; }
    public bool IsApprover { get; private set; }
    public Instant UpdatedAt { get; private set; }

    private OrganizationApprover() { }

    internal static OrganizationApprover Create(Guid organizationId, Guid userId, bool isApprover, Instant updatedAt)
    {
        if (organizationId == Guid.Empty || userId == Guid.Empty)
        {
            throw new DomainException("Organization approver identity must be valid.");
        }

        return new OrganizationApprover
        {
            OrganizationId = organizationId,
            UserId = userId,
            IsApprover = isApprover,
            UpdatedAt = updatedAt,
        };
    }

    internal bool Apply(bool isApprover, Instant updatedAt)
    {
        if (updatedAt < UpdatedAt || (updatedAt == UpdatedAt && (isApprover == IsApprover || isApprover)))
        {
            return false;
        }

        IsApprover = isApprover;
        UpdatedAt = updatedAt;
        return true;
    }
}
