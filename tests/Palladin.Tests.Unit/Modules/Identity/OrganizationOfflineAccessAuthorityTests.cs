using System.Security.Claims;
using Palladin.Core.Security;
using Palladin.Module.Identity.Contracts.ValueObjects;
using Shouldly;

namespace Palladin.Tests.Unit.Modules.Identity;

public sealed class OrganizationOfflineAccessAuthorityTests
{
    [Fact]
    public void When_AllSignedClaimsAreCanonical_Then_AuthorityIsParsed()
    {
        var principal = Principal("9", "2", "17");

        var authority = OrganizationOfflineAccessAuthority.FromAuthenticatedPrincipal(principal);

        authority.ShouldBe(new OrganizationOfflineAccessAuthority(
            9,
            OrganizationOfflineAccessPolicy.FourHours,
            17));
    }

    [Theory]
    [InlineData("0", "2", "17")]
    [InlineData("9", "4", "17")]
    [InlineData("9", "+2", "17")]
    [InlineData("9", "2", "0")]
    [InlineData("9", "2", "+17")]
    public void When_AuthorityClaimIsInvalid_Then_ParsingFailsClosed(
        string membershipGeneration,
        string policy,
        string policyVersion)
    {
        var principal = Principal(membershipGeneration, policy, policyVersion);

        OrganizationOfflineAccessAuthority.FromAuthenticatedPrincipal(principal).ShouldBeNull();
    }

    private static ClaimsPrincipal Principal(
        string membershipGeneration,
        string policy,
        string policyVersion) => new(new ClaimsIdentity(
        [
            new Claim(JwtClaimNames.AuthorizationVersion, membershipGeneration),
            new Claim(JwtClaimNames.OrganizationOfflineAccessPolicy, policy),
            new Claim(JwtClaimNames.OrganizationOfflineAccessPolicyVersion, policyVersion),
        ],
        "test"));
}
