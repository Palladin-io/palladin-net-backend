using Palladin.Core.Events;
using Palladin.Core.Types;
using Palladin.Module.Identity.Contracts.ValueObjects;
using Palladin.Module.Identity.Contracts.Events;
using NodaTime;

namespace Palladin.Module.Identity.Domain;

internal sealed class Organization : EventEntityBase
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public PlanType PlanType { get; private set; }
    public int SeatLimit { get; private set; } = 1;
    public Instant CreatedAt { get; private set; }
    internal ulong MembershipVersion { get; private set; }
    internal OrganizationOfflineAccessPolicy OfflineAccessPolicy { get; private set; }
    internal uint OfflineAccessPolicyVersion { get; private set; }

    // Org-level onboarding milestones — done for every member once anyone reaches them.
    public bool ApiKeyCreated { get; private set; }
    public bool AgentEnrolled { get; private set; }

    public ICollection<User> Users { get; private set; } = [];
    public ICollection<Role> Roles { get; private set; } = [];
    public ICollection<OrganizationMember> Members { get; private set; } = [];
    public ICollection<OrganizationInvitation> Invitations { get; private set; } = [];

    private Organization() { }

    internal bool MarkOnboardingStep(OnboardingStep step)
    {
        switch (step)
        {
            case OnboardingStep.ApiKeyCreated when !ApiKeyCreated:
                ApiKeyCreated = true;
                return true;
            case OnboardingStep.AgentEnrolled when !AgentEnrolled:
                AgentEnrolled = true;
                return true;
            default:
                return false;
        }
    }

    internal static Organization Create(
        Guid id,
        string name,
        PlanType planType,
        Guid createdBy,
        string createdByName,
        Instant now)
    {
        var organization = new Organization
        {
            Id = id,
            Name = name,
            PlanType = planType,
            SeatLimit = 1,
            CreatedAt = now,
            MembershipVersion = 1,
            OfflineAccessPolicy = OrganizationOfflineAccessPolicy.TwentyFourHours,
            OfflineAccessPolicyVersion = 1,
        };

        organization.AddEvent(new OrganizationCreatedEvent(id, name, createdBy, createdByName, now));

        return organization;
    }

    internal void UpdateName(string name, Guid updatedBy, string updatedByName, Instant now)
    {
        if (name == Name)
        {
            return;
        }

        Name = name;
        AddEvent(new OrganizationUpdatedEvent(Id, name, updatedBy, updatedByName, ["name"], now));
    }

    internal void FenceMembershipMutation()
    {
        if (MembershipVersion == ulong.MaxValue)
        {
            throw new Palladin.Core.Types.Exceptions.DomainException(
                "Organization membership version namespace is exhausted.");
        }

        MembershipVersion++;
    }

    internal bool SetOfflineAccessPolicy(
        OrganizationOfflineAccessPolicy policy,
        Guid updatedBy,
        string updatedByName,
        Instant now)
    {
        if (!Enum.IsDefined(policy))
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }

        if (OfflineAccessPolicy == policy)
        {
            return false;
        }

        if (OfflineAccessPolicyVersion == uint.MaxValue)
        {
            throw new InvalidOperationException("Organization offline-access policy version namespace is exhausted.");
        }

        OfflineAccessPolicy = policy;
        OfflineAccessPolicyVersion++;
        AddEvent(new OrganizationUpdatedEvent(
            Id,
            Name,
            updatedBy,
            updatedByName,
            ["offlineAccessPolicy"],
            now));
        return true;
    }
}
