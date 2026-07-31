using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Contracts.ValueObjects;
using NodaTime;

namespace Palladin.Tests.Integrations.Shared.Fakers;

internal static class OrganizationFaker
{
    public static PrivateCtorFaker<Organization> Create(Guid? id = null) =>
        (PrivateCtorFaker<Organization>)new PrivateCtorFaker<Organization>()
            .RuleFor(x => x.Id, id ?? Guid.NewGuid())
            .RuleFor(x => x.Name, f => f.Company.CompanyName())
            .RuleFor(x => x.PlanType, PlanType.Basic)
            .RuleFor(x => x.CreatedAt, SystemClock.Instance.GetCurrentInstant());
}
