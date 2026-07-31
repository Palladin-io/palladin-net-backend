using Palladin.Module.Identity.Domain;
using NodaTime;

namespace Palladin.Tests.Integrations.Shared.Fakers;

internal static class UserFaker
{
    public static PrivateCtorFaker<User> Create(Guid? id = null, Guid? organizationId = null, string? email = null) =>
        (PrivateCtorFaker<User>)new PrivateCtorFaker<User>()
            .RuleFor(x => x.Id, id ?? Guid.NewGuid())
            .RuleFor(x => x.Email, f => email ?? $"{Guid.NewGuid():N}@{f.Internet.DomainName()}")
            .RuleFor(x => x.DisplayName, f => f.Name.FullName())
            .RuleFor(x => x.AvatarUrl, f => f.Internet.Avatar())
            .RuleFor(x => x.OrganizationId, organizationId ?? Guid.NewGuid())
            // Verified by default (OAuth users always are); the email-verification gate tests opt into
            // unverified explicitly. Keeps every other suite's seeded users past the gate.
            .RuleFor(x => x.EmailVerified, true)
            .RuleFor(x => x.CreatedAt, SystemClock.Instance.GetCurrentInstant())
            .RuleFor(x => x.UpdatedAt, SystemClock.Instance.GetCurrentInstant());

    public static PrivateCtorFaker<User> CreateOnboarded(
        Guid? id = null,
        Guid? organizationId = null,
        string? email = null) =>
        (PrivateCtorFaker<User>)(Create(id, organizationId, email)
            .RuleFor(x => x.IsOnboarded, true)
            .RuleFor(x => x.SecurityVersion, IdentityKdfProfiles.CurrentSecurityVersion)
            .RuleFor(x => x.MinimumSecurityVersion, IdentityKdfProfiles.CurrentSecurityVersion)
            .RuleFor(x => x.KdfProfileId, IdentityKdfProfiles.CurrentProfileId)
            .RuleFor(x => x.PrivateKeyWrapRevision, 1u));
}
