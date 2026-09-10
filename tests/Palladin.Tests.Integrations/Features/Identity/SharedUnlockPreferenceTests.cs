using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Palladin.Core.Security;
using Palladin.Module.Identity.Contracts.ValueObjects;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Features;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Identity;

[Collection<ApiFactoryCollection>]
public sealed class SharedUnlockPreferenceTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_TwoClientsUseOneAccount_Then_TheyReadTheSameDefaultAndSavedChoice()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var web = apiFactory.CreateAuthenticatedClient(user, Permission.None);
        var extension = apiFactory.CreateAuthenticatedClient(user, Permission.None);

        // When
        var initial = await web.GetAsync("api/account/shared-unlock");
        var update = await extension.PutAsJsonAsync("api/account/shared-unlock",
            new UpdateSharedUnlockPreferenceRequest { SharedUnlockEnabled = false, ExpectedRevision = 1 });
        var persisted = await web.GetFromJsonAsync<SharedUnlockPreferenceResponse>("api/account/shared-unlock");

        // Then
        initial.StatusCode.ShouldBe(HttpStatusCode.OK);
        initial.Headers.CacheControl!.NoStore.ShouldBeTrue();
        (await initial.Content.ReadFromJsonAsync<SharedUnlockPreferenceResponse>())
            .ShouldBe(new SharedUnlockPreferenceResponse(true, 1));
        update.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await update.Content.ReadFromJsonAsync<SharedUnlockPreferenceResponse>())
            .ShouldBe(new SharedUnlockPreferenceResponse(false, 2));
        persisted.ShouldBe(new SharedUnlockPreferenceResponse(false, 2));
    }

    [Fact]
    public async Task When_OneAccountUsesTwoOrganizations_Then_PreferenceRemainsAccountWide()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var (_, secondOrganization, secondRole) = await apiFactory.Services.SeedUserAsync();
        var firstClient = apiFactory.CreateAuthenticatedClient(user);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
        var membership = OrganizationMember.Create(secondOrganization.Id, user.Id, secondRole,
            user.DisplayName, user.Email, apiFactory.FakeClock.GetCurrentInstant());
        context.Attach(secondRole);
        context.OrganizationMembers.Add(membership);
        await context.SaveChangesAsync();
        var secondClient = apiFactory.CreateClient();
        secondClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            scope.ServiceProvider.GetRequiredService<ITokenService>().GenerateAccessToken(
                user, secondOrganization.Id, Permission.None, PlanType.Basic,
                membership.AuthorizationVersion, secondOrganization.OfflineAccessPolicy,
                secondOrganization.OfflineAccessPolicyVersion));

        // When
        var response = await secondClient.PutAsJsonAsync("api/account/shared-unlock",
            new UpdateSharedUnlockPreferenceRequest { SharedUnlockEnabled = false, ExpectedRevision = 1 });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await firstClient.GetFromJsonAsync<SharedUnlockPreferenceResponse>("api/account/shared-unlock"))
            .ShouldBe(new SharedUnlockPreferenceResponse(false, 2));
    }

    [Fact]
    public async Task When_FalseWasAlreadySaved_Then_DatabaseDefaultDoesNotReplaceIt()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync(UserFaker.Create()
            .RuleFor(user => user.SharedUnlockEnabled, false)
            .RuleFor(user => user.SharedUnlockRevision, 7u));
        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var result = await client.GetFromJsonAsync<SharedUnlockPreferenceResponse>("api/account/shared-unlock");

        // Then
        result.ShouldBe(new SharedUnlockPreferenceResponse(false, 7));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task When_EnableArrivesAfterAcceptedOff_Then_StaleRevisionCannotUndoOff(bool initiallyEnabled)
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync(UserFaker.Create()
            .RuleFor(user => user.SharedUnlockEnabled, initiallyEnabled));
        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var off = await client.PutAsJsonAsync("api/account/shared-unlock",
            new UpdateSharedUnlockPreferenceRequest { SharedUnlockEnabled = false, ExpectedRevision = 1 });
        var staleEnable = await client.PutAsJsonAsync("api/account/shared-unlock",
            new UpdateSharedUnlockPreferenceRequest { SharedUnlockEnabled = true, ExpectedRevision = 1 });
        var persisted = await client.GetFromJsonAsync<SharedUnlockPreferenceResponse>("api/account/shared-unlock");

        // Then
        off.StatusCode.ShouldBe(HttpStatusCode.OK);
        staleEnable.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await staleEnable.Content.ReadAsStringAsync()).ShouldContain("shared-unlock-preference-conflict");
        persisted.ShouldBe(new SharedUnlockPreferenceResponse(false, 2));
    }

    [Fact]
    public async Task When_RequestsRaceWithOneRevision_Then_ExactlyOneCommits()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var web = apiFactory.CreateAuthenticatedClient(user);
        var extension = apiFactory.CreateAuthenticatedClient(user);

        // When
        var responses = await Task.WhenAll(
            web.PutAsJsonAsync("api/account/shared-unlock",
                new UpdateSharedUnlockPreferenceRequest { SharedUnlockEnabled = false, ExpectedRevision = 1 }),
            extension.PutAsJsonAsync("api/account/shared-unlock",
                new UpdateSharedUnlockPreferenceRequest { SharedUnlockEnabled = true, ExpectedRevision = 1 }));

        // Then
        responses.Count(response => response.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        responses.Count(response => response.StatusCode == HttpStatusCode.Conflict).ShouldBe(1);
        var winner = await responses.Single(response => response.IsSuccessStatusCode)
            .Content.ReadFromJsonAsync<SharedUnlockPreferenceResponse>();
        winner!.Revision.ShouldBe(2u);
        (await web.GetFromJsonAsync<SharedUnlockPreferenceResponse>("api/account/shared-unlock")).ShouldBe(winner);
    }

    [Fact]
    public async Task When_StaleTrackedEnableCommitsAfterOffAtSameClockInstant_Then_DatabaseFenceRejectsIt()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        await using var offScope = apiFactory.Services.CreateAsyncScope();
        await using var enableScope = apiFactory.Services.CreateAsyncScope();
        var offContext = offScope.ServiceProvider.GetRequiredService<IdentityDomainWriteContext>();
        var enableContext = enableScope.ServiceProvider.GetRequiredService<IdentityDomainWriteContext>();
        var offUser = await offContext.Users.SingleAsync(candidate => candidate.Id == user.Id);
        var enableUser = await enableContext.Users.SingleAsync(candidate => candidate.Id == user.Id);

        // When
        offUser.TrySetSharedUnlockPreference(false, 1, user.UpdatedAt).ShouldBeTrue();
        enableUser.TrySetSharedUnlockPreference(true, 1, user.UpdatedAt).ShouldBeTrue();
        await offContext.CommitAsync();

        // Then
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => enableContext.CommitAsync());
        var client = apiFactory.CreateAuthenticatedClient(user);
        (await client.GetFromJsonAsync<SharedUnlockPreferenceResponse>("api/account/shared-unlock"))
            .ShouldBe(new SharedUnlockPreferenceResponse(false, 2));
    }

    [Fact]
    public async Task When_PayloadOrQueryNamesAnotherUser_Then_OnlyAuthenticatedAccountIsUsed()
    {
        // Given
        var (caller, _, _) = await apiFactory.Services.SeedUserAsync();
        var (other, _, _) = await apiFactory.Services.SeedUserAsync();
        var callerClient = apiFactory.CreateAuthenticatedClient(caller);
        var otherClient = apiFactory.CreateAuthenticatedClient(other);

        // When
        var response = await callerClient.PutAsJsonAsync("api/account/shared-unlock",
            new { sharedUnlockEnabled = false, expectedRevision = 1, userId = other.Id, organizationId = other.OrganizationId });
        var queried = await callerClient.GetFromJsonAsync<SharedUnlockPreferenceResponse>(
            $"api/account/shared-unlock?userId={other.Id}");
        var otherPreference = await otherClient.GetFromJsonAsync<SharedUnlockPreferenceResponse>("api/account/shared-unlock");

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        queried.ShouldBe(new SharedUnlockPreferenceResponse(false, 2));
        otherPreference.ShouldBe(new SharedUnlockPreferenceResponse(true, 1));
    }

    [Fact]
    public async Task When_NoUserSession_Then_ReadAndWriteRequireAuthentication()
    {
        // Given
        var client = apiFactory.CreateClient();

        // When
        var read = await client.GetAsync("api/account/shared-unlock");
        var write = await client.PutAsJsonAsync("api/account/shared-unlock",
            new UpdateSharedUnlockPreferenceRequest { SharedUnlockEnabled = false, ExpectedRevision = 1 });

        // Then
        read.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        write.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(null, 1u)]
    [InlineData(false, null)]
    [InlineData(false, 0u)]
    public async Task When_ChoiceOrRevisionIsMissing_Then_NoPreferenceIsChanged(bool? enabled, uint? revision)
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var response = await client.PutAsJsonAsync("api/account/shared-unlock",
            new UpdateSharedUnlockPreferenceRequest { SharedUnlockEnabled = enabled, ExpectedRevision = revision });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.GetFromJsonAsync<SharedUnlockPreferenceResponse>("api/account/shared-unlock"))
            .ShouldBe(new SharedUnlockPreferenceResponse(true, 1));
    }
}
