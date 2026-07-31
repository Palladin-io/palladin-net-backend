using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Features;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Seeders;
using Bogus;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Identity;

// Onboarding flags are materialized per-user in Identity (fed by MarkOnboardingStepCommand), so these
// tests seed the User's own state directly. The event -> command -> state path is covered by
// OnboardingTests.
[Collection<ApiFactoryCollection>]
public sealed class GetAccountTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_AuthenticatedUser_WithoutKeys_Then_ReturnsAccountWithIsOnboardedFalse()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var response = await client.GetAsync("api/account");

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<GetAccountResponse>();
        result.ShouldNotBeNull();
        result.UserId.ShouldBe(user.Id);
        result.Email.ShouldBe(user.Email);
        result.DisplayName.ShouldBe(user.DisplayName);
        result.IsOnboarded.ShouldBeFalse();
        result.MemberKeyVersion.ShouldBeNull();
        result.Salt.ShouldBeNull();
        result.EncryptedPrivateKey.ShouldBeNull();
    }

    [Fact]
    public async Task When_AuthenticatedUser_WithKeys_Then_ReturnsCanonicalBase64UrlWithoutPadding()
    {
        // Given
        byte[] salt = [0xFA, 0xFE];
        byte[] encryptedPrivateKey = [0xF9, 0xFD];
        byte[] recoverySalt = [0xF8, 0xFC];
        byte[] encryptedPrivateKeyByRecovery = [0xF7, 0xFB];
        var userFaker = UserFaker.CreateOnboarded()
            .RuleFor(x => x.PublicKey, [0x07, 0x08, 0x09])
            .RuleFor(x => x.MemberKeyVersion, 7u)
            .RuleFor(x => x.Salt, salt)
            .RuleFor(x => x.EncryptedPrivateKey, encryptedPrivateKey)
            .RuleFor(x => x.RecoverySalt, recoverySalt)
            .RuleFor(x => x.EncryptedPrivateKeyByRecovery, encryptedPrivateKeyByRecovery);
        var (user, _, _) = await apiFactory.Services.SeedUserAsync(userFaker);
        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var response = await client.GetAsync("api/account");

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<GetAccountResponse>();
        result.ShouldNotBeNull();
        result.IsOnboarded.ShouldBeTrue();
        result.MemberKeyVersion.ShouldBe(7u);
        result.Salt.ShouldBe(WebEncoders.Base64UrlEncode(salt));
        result.EncryptedPrivateKey.ShouldBe(WebEncoders.Base64UrlEncode(encryptedPrivateKey));
        result.RecoverySalt.ShouldBe(WebEncoders.Base64UrlEncode(recoverySalt));
        result.EncryptedPrivateKeyByRecovery.ShouldBe(WebEncoders.Base64UrlEncode(encryptedPrivateKeyByRecovery));
    }

    [Fact]
    public async Task When_Unauthenticated_Then_Returns401()
    {
        // Given
        var client = apiFactory.CreateClient();

        // When
        var response = await client.GetAsync("api/account");

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task When_NoOnboardingProgress_Then_OnboardingStepsAllFalse()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var response = await client.GetAsync("api/account");

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<GetAccountResponse>();
        result.ShouldNotBeNull();
        result.OnboardingSteps.ShouldNotBeNull();
        result.OnboardingSteps.EntryCreated.ShouldBeFalse();
        result.OnboardingSteps.ApiKeyCreated.ShouldBeFalse();
        result.OnboardingSteps.AgentEnrolled.ShouldBeFalse();
        result.OnboardingSteps.MobileRegistered.ShouldBeFalse();
    }

    [Fact]
    public async Task When_EntryMilestoneReached_Then_OnlyEntryCreatedIsTrue()
    {
        // Given
        var result = await GetAccountForUserWithSteps(entryCreated: true);

        // Then
        result.OnboardingSteps.EntryCreated.ShouldBeTrue();
        result.OnboardingSteps.ApiKeyCreated.ShouldBeFalse();
        result.OnboardingSteps.AgentEnrolled.ShouldBeFalse();
    }

    [Fact]
    public async Task When_ApiKeyMilestoneReached_Then_OnlyApiKeyCreatedIsTrue()
    {
        // Given
        var result = await GetAccountForUserWithSteps(apiKeyCreated: true);

        // Then
        result.OnboardingSteps.EntryCreated.ShouldBeFalse();
        result.OnboardingSteps.ApiKeyCreated.ShouldBeTrue();
        result.OnboardingSteps.AgentEnrolled.ShouldBeFalse();
    }

    [Fact]
    public async Task When_AgentMilestoneReached_Then_OnlyAgentEnrolledIsTrue()
    {
        // Given
        var result = await GetAccountForUserWithSteps(agentEnrolled: true);

        // Then
        result.OnboardingSteps.EntryCreated.ShouldBeFalse();
        result.OnboardingSteps.ApiKeyCreated.ShouldBeFalse();
        result.OnboardingSteps.AgentEnrolled.ShouldBeTrue();
    }

    [Fact]
    public async Task When_AllOnboardingStepsComplete_Then_AllFlagsTrue()
    {
        // Given
        var result = await GetAccountForUserWithSteps(
            entryCreated: true, apiKeyCreated: true, agentEnrolled: true, mobileRegistered: true);

        // Then
        result.OnboardingSteps.EntryCreated.ShouldBeTrue();
        result.OnboardingSteps.ApiKeyCreated.ShouldBeTrue();
        result.OnboardingSteps.AgentEnrolled.ShouldBeTrue();
        result.OnboardingSteps.MobileRegistered.ShouldBeTrue();
    }

    [Fact]
    public async Task When_UserHasMobileDevice_Then_MobileRegisteredIsTrue()
    {
        // Given
        var result = await GetAccountForUserWithSteps(mobileRegistered: true);

        // Then
        result.OnboardingSteps.MobileRegistered.ShouldBeTrue();
        result.OnboardingSteps.EntryCreated.ShouldBeFalse();
        result.OnboardingSteps.ApiKeyCreated.ShouldBeFalse();
        result.OnboardingSteps.AgentEnrolled.ShouldBeFalse();
    }

    [Fact]
    public async Task When_UserHasNoMobileDevice_Then_MobileRegisteredIsFalse()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var response = await client.GetAsync("api/account");

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<GetAccountResponse>();
        result.ShouldNotBeNull();
        result.OnboardingSteps.MobileRegistered.ShouldBeFalse();
    }

    // entryCreated + mobileRegistered are per-user (on User); apiKeyCreated + agentEnrolled are
    // org-level (on Organization) — done for every member once anyone reaches them.
    private async Task<GetAccountResponse> GetAccountForUserWithSteps(
        bool entryCreated = false,
        bool apiKeyCreated = false,
        bool agentEnrolled = false,
        bool mobileRegistered = false)
    {
        var userFaker = (Faker<User>)UserFaker.Create()
            .RuleFor(x => x.EntryCreated, entryCreated)
            .RuleFor(x => x.MobileRegistered, mobileRegistered);
        var orgFaker = (Faker<Organization>)OrganizationFaker.Create()
            .RuleFor(x => x.ApiKeyCreated, apiKeyCreated)
            .RuleFor(x => x.AgentEnrolled, agentEnrolled);
        var (user, _, _) = await apiFactory.Services.SeedUserAsync(userFaker, orgFaker);
        var client = apiFactory.CreateAuthenticatedClient(user);

        var response = await client.GetAsync("api/account");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<GetAccountResponse>();
        return result.ShouldNotBeNull();
    }

    [Fact]
    public async Task When_SecondUserInOrgThatAlreadyHasAgentAndApiKey_Then_ThoseStepsAreDoneForThem()
    {
        // Given — an org whose org-level onboarding is already complete (via its first user)
        var orgFaker = (Faker<Organization>)OrganizationFaker.Create()
            .RuleFor(x => x.ApiKeyCreated, true)
            .RuleFor(x => x.AgentEnrolled, true);
        var (firstUser, org, _) = await apiFactory.Services.SeedUserAsync(organizationFaker: orgFaker);

        // A brand-new user joins the SAME org (no personal steps of their own)
        var secondUser = await apiFactory.Services.SeedAdditionalOrganizationMemberAsync(org.Id);
        var client = apiFactory.CreateAuthenticatedClient(secondUser);

        // When
        var response = await client.GetAsync("api/account");

        // Then — org-level steps are already done for the second user; per-user steps are not
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<GetAccountResponse>();
        result.ShouldNotBeNull();
        result.OnboardingSteps.ApiKeyCreated.ShouldBeTrue();
        result.OnboardingSteps.AgentEnrolled.ShouldBeTrue();
        result.OnboardingSteps.EntryCreated.ShouldBeFalse();
        result.OnboardingSteps.MobileRegistered.ShouldBeFalse();
        firstUser.Id.ShouldNotBe(secondUser.Id);
    }
}
