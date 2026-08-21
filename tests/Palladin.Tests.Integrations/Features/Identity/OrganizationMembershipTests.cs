using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Text.Json;
using Bogus;
using Palladin.Core.Security;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Features;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Shared;
using Palladin.Module.Identity.Triggers;
using Palladin.Module.Vault.Contracts.Commands;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Seeders;
using Palladin.Tests.Integrations.Shared.Mocks;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NodaTime.Text;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Identity;

[Collection<ApiFactoryCollection>]
public sealed class OrganizationMembershipTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_OrdinaryMemberListsMembers_Then_ReturnsOnlyActiveOrganization()
    {
        // Given
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var member = await apiFactory.Services.SeedAdditionalOrganizationMemberAsync(organization.Id);
        var (otherOwner, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(member, Permission.GrantManage);

        // When
        var (response, result) = await client.GETAsync<
            ListOrganizationMembersEndpoint,
            ListOrganizationMembersResponse>();

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.Items.Select(x => x.UserId).ShouldBe([owner.Id, member.Id], ignoreOrder: true);
        result.Items.Select(x => x.UserId).ShouldNotContain(otherOwner.Id);
    }

    [Fact]
    public async Task When_ListingOrganizations_Then_ReturnsHomeOrganizationAsOwnerAndActive()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var (response, result) = await client.GETAsync<
            ListOrganizationsEndpoint,
            ListOrganizationsResponse>();

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var item = result.Items.ShouldHaveSingleItem();
        item.Id.ShouldBe(organization.Id);
        item.Roles.ShouldHaveSingleItem().Name.ShouldBe("Administrator");
        item.EffectivePermissions.ShouldBe(int.MaxValue);
        item.IsOwner.ShouldBeTrue();
        item.IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task When_InvitingMember_Then_StoresOnlyTokenHash()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var role = await CreateRoleAsync(organization.Id, "Invited Member", Permission.None);
        await SetSeatLimitAsync(organization.Id, 2);
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.AddUser);

        // When
        var response = await client.POSTAsync<InviteOrganizationMemberEndpoint, InviteOrganizationMemberRequest>(
            new InviteOrganizationMemberRequest { Email = "invitee@example.com", RoleId = role.Id });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        var invitation = await readContext.OrganizationInvitations
            .SingleAsync(i => i.OrganizationId == organization.Id && i.Email == "invitee@example.com");
        invitation.TokenHash.ShouldNotBeNullOrWhiteSpace();
        invitation.TokenHash.ShouldNotContain("invitee@example.com");
    }

    [Fact]
    public async Task When_OrganizationHasNoAvailableSeat_Then_InviteReturns409()
    {
        // Given
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var role = await CreateRoleAsync(organization.Id, "Invited Member", Permission.None);
        var client = apiFactory.CreateAuthenticatedClient(owner, Permission.AddUser);

        // When
        var response = await client.POSTAsync<InviteOrganizationMemberEndpoint, InviteOrganizationMemberRequest>(
            new InviteOrganizationMemberRequest { Email = "no-seat@example.com", RoleId = role.Id });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).ShouldContain("organization-seat-limit-reached");

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        (await readContext.OrganizationInvitations.AnyAsync(
            invitation => invitation.OrganizationId == organization.Id)).ShouldBeFalse();
    }

    [Fact]
    public async Task When_PendingInvitationReservesLastSeat_Then_NextInviteReturns409()
    {
        // Given
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var role = await CreateRoleAsync(organization.Id, "Invited Member", Permission.None);
        await SetSeatLimitAsync(organization.Id, 2);
        var client = apiFactory.CreateAuthenticatedClient(owner, Permission.AddUser);

        // When
        var firstResponse = await client.POSTAsync<InviteOrganizationMemberEndpoint, InviteOrganizationMemberRequest>(
            new InviteOrganizationMemberRequest { Email = "first@example.com", RoleId = role.Id });
        var secondResponse = await client.POSTAsync<InviteOrganizationMemberEndpoint, InviteOrganizationMemberRequest>(
            new InviteOrganizationMemberRequest { Email = "second@example.com", RoleId = role.Id });

        // Then
        firstResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        secondResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await secondResponse.Content.ReadAsStringAsync()).ShouldContain("organization-seat-limit-reached");

        var (_, organizationDetails) = await client.GETAsync<GetOrgEndpoint, GetOrgResponse>();
        organizationDetails.MemberCount.ShouldBe(1);
        organizationDetails.SeatUsage.ShouldBe(2);
        organizationDetails.SeatLimit.ShouldBe(2);
    }

    [Fact]
    public async Task When_ListingInvitations_Then_ReturnsOnlyPendingInvitationsFromActiveOrganization()
    {
        // Given
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var role = await CreateRoleAsync(organization.Id, "Invited Member", Permission.None);
        await SetSeatLimitAsync(organization.Id, 3);
        var client = apiFactory.CreateAuthenticatedClient(owner, Permission.AddUser);
        var (otherOwner, otherOrganization, _) = await apiFactory.Services.SeedUserAsync();
        await SeedInvitationAsync(
            otherOwner,
            otherOrganization,
            "other-tenant@example.com",
            "other-tenant-invitation-token");
        await SeedInvitationAsync(
            owner,
            organization,
            "expired@example.com",
            "expired-list-invitation-token",
            Duration.FromHours(-1));
        var inviteResponse = await client.POSTAsync<
            InviteOrganizationMemberEndpoint,
            InviteOrganizationMemberRequest>(new InviteOrganizationMemberRequest
            {
                Email = "pending@example.com",
                RoleId = role.Id,
            });

        // When
        var (response, result) = await client.GETAsync<
            ListOrganizationInvitationsEndpoint,
            ListOrganizationInvitationsResponse>();

        // Then
        inviteResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var invitation = result.Items.ShouldHaveSingleItem();
        invitation.Email.ShouldBe("pending@example.com");
        invitation.RoleId.ShouldBe(role.Id);
        invitation.RoleName.ShouldBe(role.Name);
        invitation.InvitedByName.ShouldBe(owner.DisplayName);
        invitation.ExpiresAt.ShouldBeGreaterThan(invitation.CreatedAt);
        invitation.SentAt.ShouldBe(invitation.CreatedAt);
        invitation.ResendAvailableAt.ShouldBeGreaterThan(invitation.SentAt);
    }

    [Fact]
    public async Task When_ResendingPendingInvitation_Then_RotatesTokenAndRenewsExpiryWithoutUsingAnotherSeat()
    {
        // Given
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var (invitedUser, _, _) = await apiFactory.Services.SeedUserAsync();
        await SetSeatLimitAsync(organization.Id, 2);
        const string oldToken = "organization-invitation-token-before-resend";
        var issuedAt = apiFactory.FakeClock.GetCurrentInstant() - Duration.FromMinutes(2);
        var invitation = await SeedInvitationAsync(
            owner,
            organization,
            invitedUser.Email,
            oldToken,
            issuedAt: issuedAt);
        var oldTokenHash = invitation.TokenHash;
        var oldExpiry = invitation.ExpiresAt;
        var ownerClient = apiFactory.CreateAuthenticatedClient(owner, Permission.AddUser);
        var invitedUserClient = apiFactory.CreateAuthenticatedClient(invitedUser);

        // When
        var response = await ownerClient.PostAsync(
            $"api/organization/invitations/{invitation.Id}/resend",
            content: null);
        using var responseDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var result = responseDocument.RootElement;
        var sentAt = InstantPattern.ExtendedIso.Parse(result.GetProperty("sentAt").GetString()!).Value;
        var expiresAt = InstantPattern.ExtendedIso.Parse(result.GetProperty("expiresAt").GetString()!).Value;
        var resendAvailableAt = InstantPattern.ExtendedIso
            .Parse(result.GetProperty("resendAvailableAt").GetString()!).Value;
        var oldLinkResponse = await invitedUserClient.POSTAsync<
            AcceptOrganizationInvitationEndpoint,
            AcceptOrganizationInvitationRequest>(new AcceptOrganizationInvitationRequest { Token = oldToken });
        var (_, organizationDetails) = await ownerClient.GETAsync<GetOrgEndpoint, GetOrgResponse>();

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        sentAt.ShouldBe(apiFactory.FakeClock.GetCurrentInstant());
        expiresAt.ShouldBeGreaterThan(oldExpiry);
        resendAvailableAt.ShouldBeGreaterThan(sentAt);
        oldLinkResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        organizationDetails.SeatUsage.ShouldBe(2);

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var persisted = await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>()
            .OrganizationInvitations.SingleAsync(candidate => candidate.Id == invitation.Id);
        persisted.TokenHash.ShouldNotBe(oldTokenHash);
        persisted.TokenHash.ShouldNotContain(oldToken);
        persisted.CreatedAt.ShouldBe(TruncateToMicroseconds(issuedAt));
        persisted.LastSentAt.ShouldBe(TruncateToMicroseconds(sentAt));
        persisted.ExpiresAt.ShouldBe(TruncateToMicroseconds(expiresAt));
        persisted.AcceptedAt.ShouldBeNull();
        persisted.CancelledAt.ShouldBeNull();
    }

    [Fact]
    public async Task When_ResendingInvitationInsideCooldown_Then_Returns429WithoutRotatingToken()
    {
        // Given
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var invitation = await SeedInvitationAsync(
            owner,
            organization,
            "cooldown@example.com",
            "organization-invitation-cooldown-token");
        var originalTokenHash = invitation.TokenHash;
        var originalExpiry = invitation.ExpiresAt;
        var client = apiFactory.CreateAuthenticatedClient(owner, Permission.AddUser);

        // When
        var response = await client.PostAsync(
            $"api/organization/invitations/{invitation.Id}/resend",
            content: null);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await response.Content.ReadAsStringAsync())
            .ShouldContain("organization-invitation-resend-too-soon");
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var persisted = await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>()
            .OrganizationInvitations.SingleAsync(candidate => candidate.Id == invitation.Id);
        persisted.TokenHash.ShouldBe(originalTokenHash);
        persisted.ExpiresAt.ShouldBe(originalExpiry);
        persisted.LastSentAt.ShouldBe(invitation.LastSentAt);
    }

    [Fact]
    public async Task When_ResendingInvitationFromAnotherOrganization_Then_Returns404WithoutMutation()
    {
        // Given
        var (owner, _, _) = await apiFactory.Services.SeedUserAsync();
        var (otherOwner, otherOrganization, _) = await apiFactory.Services.SeedUserAsync();
        var invitation = await SeedInvitationAsync(
            otherOwner,
            otherOrganization,
            "foreign-resend@example.com",
            "foreign-resend-invitation-token",
            issuedAt: apiFactory.FakeClock.GetCurrentInstant() - Duration.FromMinutes(2));
        var originalTokenHash = invitation.TokenHash;
        var client = apiFactory.CreateAuthenticatedClient(owner, Permission.AddUser);

        // When
        var response = await client.PostAsync(
            $"api/organization/invitations/{invitation.Id}/resend",
            content: null);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var persisted = await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>()
            .OrganizationInvitations.SingleAsync(candidate => candidate.Id == invitation.Id);
        persisted.TokenHash.ShouldBe(originalTokenHash);
        persisted.LastSentAt.ShouldBe(invitation.LastSentAt);
    }

    [Fact]
    public async Task When_ResendingInvitationWhoseRoleNowHasGrantManage_Then_Returns409WithoutRotatingToken()
    {
        // Given
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        await CreateRoleAsync(organization.Id, "Unsafe invitation role", Permission.GrantManage);
        var invitation = await SeedInvitationAsync(
            owner,
            organization,
            "unsafe-resend@example.com",
            "unsafe-resend-invitation-token",
            issuedAt: apiFactory.FakeClock.GetCurrentInstant() - Duration.FromMinutes(2));
        var originalTokenHash = invitation.TokenHash;
        var originalExpiry = invitation.ExpiresAt;
        var client = apiFactory.CreateAuthenticatedClient(owner, Permission.AddUser);

        // When
        var response = await client.PostAsync(
            $"api/organization/invitations/{invitation.Id}/resend",
            content: null);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync())
            .ShouldContain("organization-role-grant-manage-cutover-unavailable");
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var persisted = await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>()
            .OrganizationInvitations.SingleAsync(candidate => candidate.Id == invitation.Id);
        persisted.TokenHash.ShouldBe(originalTokenHash);
        persisted.ExpiresAt.ShouldBe(originalExpiry);
        persisted.LastSentAt.ShouldBe(invitation.LastSentAt);
    }

    [Fact]
    public async Task When_CancellingInvitation_Then_ReleasesSeatAndInvalidatesToken()
    {
        // Given
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var (invitedUser, _, _) = await apiFactory.Services.SeedUserAsync();
        await SetSeatLimitAsync(organization.Id, 2);
        const string token = "cancelled-organization-invitation-token";
        var invitation = await SeedInvitationAsync(owner, organization, invitedUser.Email, token);
        var ownerClient = apiFactory.CreateAuthenticatedClient(owner, Permission.AddUser);
        var invitedUserClient = apiFactory.CreateAuthenticatedClient(invitedUser);

        // When
        var cancelResponse = await ownerClient.DELETEAsync<
            CancelOrganizationInvitationEndpoint,
            CancelOrganizationInvitationRequest>(new CancelOrganizationInvitationRequest
            {
                InvitationId = invitation.Id,
            });
        var repeatedCancelResponse = await ownerClient.DELETEAsync<
            CancelOrganizationInvitationEndpoint,
            CancelOrganizationInvitationRequest>(new CancelOrganizationInvitationRequest
            {
                InvitationId = invitation.Id,
            });
        var acceptResponse = await invitedUserClient.POSTAsync<
            AcceptOrganizationInvitationEndpoint,
            AcceptOrganizationInvitationRequest>(new AcceptOrganizationInvitationRequest { Token = token });
        var (_, organizationDetails) = await ownerClient.GETAsync<GetOrgEndpoint, GetOrgResponse>();

        // Then
        cancelResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        repeatedCancelResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        acceptResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        organizationDetails.MemberCount.ShouldBe(1);
        organizationDetails.SeatUsage.ShouldBe(1);
        organizationDetails.SeatLimit.ShouldBe(2);

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var persistedInvitation = await scope.ServiceProvider
            .GetRequiredService<IdentityDbReadContext>()
            .OrganizationInvitations.SingleAsync(
                candidate => candidate.Id == invitation.Id,
                TestContext.Current.CancellationToken);
        persistedInvitation.CancelledAt.ShouldNotBeNull();
        persistedInvitation.AcceptedAt.ShouldBeNull();
    }

    [Fact]
    public async Task When_CancellingInvitationFromAnotherOrganization_Then_Returns404WithoutMutation()
    {
        // Given
        var (owner, _, _) = await apiFactory.Services.SeedUserAsync();
        var (otherOwner, otherOrganization, _) = await apiFactory.Services.SeedUserAsync();
        var invitation = await SeedInvitationAsync(
            otherOwner,
            otherOrganization,
            "foreign-invitation@example.com",
            "foreign-organization-invitation-token");
        var client = apiFactory.CreateAuthenticatedClient(owner, Permission.AddUser);

        // When
        var response = await client.DELETEAsync<
            CancelOrganizationInvitationEndpoint,
            CancelOrganizationInvitationRequest>(new CancelOrganizationInvitationRequest
            {
                InvitationId = invitation.Id,
            });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var persistedInvitation = await scope.ServiceProvider
            .GetRequiredService<IdentityDbReadContext>()
            .OrganizationInvitations.SingleAsync(
                candidate => candidate.Id == invitation.Id,
                TestContext.Current.CancellationToken);
        persistedInvitation.CancelledAt.ShouldBeNull();
        persistedInvitation.AcceptedAt.ShouldBeNull();
    }

    [Fact]
    public async Task When_UpdatingPendingInvitationRole_Then_ReplacesItsInitialRoleWithoutChangingSeatUsage()
    {
        // Given
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var originalRole = await CreateRoleAsync(organization.Id, "Original invitation role", Permission.None);
        var replacementRole = await CreateRoleAsync(
            organization.Id,
            "Replacement invitation role",
            Permission.VaultCreate | Permission.VaultManage);
        await SetSeatLimitAsync(organization.Id, 2);
        var client = apiFactory.CreateAuthenticatedClient(owner, Permission.AddUser);
        var inviteResponse = await client.POSTAsync<
            InviteOrganizationMemberEndpoint,
            InviteOrganizationMemberRequest>(new InviteOrganizationMemberRequest
            {
                Email = "role-update@example.com",
                RoleId = originalRole.Id,
            });
        await using var lookupScope = apiFactory.Services.CreateAsyncScope();
        var invitationId = await lookupScope.ServiceProvider
            .GetRequiredService<IdentityDbReadContext>()
            .OrganizationInvitations
            .Where(invitation => invitation.OrganizationId == organization.Id
                                 && invitation.Email == "role-update@example.com")
            .Select(invitation => invitation.Id)
            .SingleAsync();

        // When
        var response = await client.PUTAsync<
            UpdateOrganizationInvitationRoleEndpoint,
            UpdateOrganizationInvitationRoleRequest>(new UpdateOrganizationInvitationRoleRequest
            {
                InvitationId = invitationId,
                RoleId = replacementRole.Id,
            });
        var (_, organizationDetails) = await client.GETAsync<GetOrgEndpoint, GetOrgResponse>();

        // Then
        inviteResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        organizationDetails.SeatUsage.ShouldBe(2);
        await using var verificationScope = apiFactory.Services.CreateAsyncScope();
        var persisted = await verificationScope.ServiceProvider
            .GetRequiredService<IdentityDbReadContext>()
            .OrganizationInvitations.SingleAsync(invitation => invitation.Id == invitationId);
        persisted.RoleId.ShouldBe(replacementRole.Id);
        persisted.RoleName.ShouldBe(replacementRole.Name);
        persisted.AcceptedAt.ShouldBeNull();
        persisted.CancelledAt.ShouldBeNull();
    }

    [Fact]
    public async Task When_UpdatingPendingInvitationToGrantManageRole_Then_Returns409WithoutMutation()
    {
        // Given
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var invitation = await SeedInvitationAsync(
            owner,
            organization,
            "unsafe-role-update@example.com",
            "unsafe-role-update-token");
        var unsafeRole = await CreateRoleAsync(
            organization.Id,
            "Unsafe invitation role",
            Permission.GrantManage);
        var client = apiFactory.CreateAuthenticatedClient(owner, Permission.AddUser);

        // When
        var response = await client.PUTAsync<
            UpdateOrganizationInvitationRoleEndpoint,
            UpdateOrganizationInvitationRoleRequest>(new UpdateOrganizationInvitationRoleRequest
            {
                InvitationId = invitation.Id,
                RoleId = unsafeRole.Id,
            });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync())
            .ShouldContain("organization-role-grant-manage-cutover-unavailable");
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var persisted = await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>()
            .OrganizationInvitations.SingleAsync(candidate => candidate.Id == invitation.Id);
        persisted.RoleId.ShouldBe(invitation.RoleId);
        persisted.RoleName.ShouldBe(invitation.RoleName);
    }

    [Fact]
    public async Task When_UpdatingInvitationFromAnotherOrganization_Then_Returns404WithoutMutation()
    {
        // Given
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var (otherOwner, otherOrganization, _) = await apiFactory.Services.SeedUserAsync();
        var invitation = await SeedInvitationAsync(
            otherOwner,
            otherOrganization,
            "foreign-role-update@example.com",
            "foreign-role-update-token");
        var localRole = await CreateRoleAsync(organization.Id, "Local role", Permission.None);
        var client = apiFactory.CreateAuthenticatedClient(owner, Permission.AddUser);

        // When
        var response = await client.PUTAsync<
            UpdateOrganizationInvitationRoleEndpoint,
            UpdateOrganizationInvitationRoleRequest>(new UpdateOrganizationInvitationRoleRequest
            {
                InvitationId = invitation.Id,
                RoleId = localRole.Id,
            });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var persisted = await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>()
            .OrganizationInvitations.SingleAsync(candidate => candidate.Id == invitation.Id);
        persisted.RoleId.ShouldBe(invitation.RoleId);
        persisted.RoleName.ShouldBe(invitation.RoleName);
    }

    [Fact]
    public async Task When_ConcurrentInvitesCompeteForLastSeat_Then_OnlyOneIsCreated()
    {
        // Given
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var role = await CreateRoleAsync(organization.Id, "Invited Member", Permission.None);
        await SetSeatLimitAsync(organization.Id, 2);
        var client = apiFactory.CreateAuthenticatedClient(owner, Permission.AddUser);

        // When
        var responses = await Task.WhenAll(
            client.POSTAsync<InviteOrganizationMemberEndpoint, InviteOrganizationMemberRequest>(
                new InviteOrganizationMemberRequest { Email = "concurrent-one@example.com", RoleId = role.Id }),
            client.POSTAsync<InviteOrganizationMemberEndpoint, InviteOrganizationMemberRequest>(
                new InviteOrganizationMemberRequest { Email = "concurrent-two@example.com", RoleId = role.Id }));

        // Then
        responses.Count(response => response.StatusCode == HttpStatusCode.NoContent).ShouldBe(1);
        responses.Count(response => response.StatusCode == HttpStatusCode.Conflict).ShouldBe(1);

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        (await readContext.OrganizationInvitations.CountAsync(
            invitation => invitation.OrganizationId == organization.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task When_ListingRoles_Then_ReturnsSystemAndCustomRolesWithPermissions()
    {
        // Given
        var (owner, organization, administratorRole) = await apiFactory.Services.SeedUserAsync();
        var customRole = await CreateRoleAsync(organization.Id, "Security Auditor", Permission.AuditView);
        var client = apiFactory.CreateAuthenticatedClient(owner);

        // When
        var (response, result) = await client.GETAsync<
            ListOrganizationRolesEndpoint,
            ListOrganizationRolesResponse>();

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.Items.Count.ShouldBe(3);
        result.Items.Single(role => role.Id == administratorRole.Id).Permissions.ShouldBe(int.MaxValue);
        var defaultUserRole = result.Items.Single(role => role.Name == Role.DefaultUserName);
        defaultUserRole.Permissions.ShouldBe((int)Role.DefaultUserPermissions);
        defaultUserRole.IsSystem.ShouldBeTrue();
        result.Items.Single(role => role.Id == customRole.Id).Permissions.ShouldBe((int)Permission.AuditView);
    }

    [Fact]
    public async Task When_InvitingMemberWithDefaultUserRole_Then_StoresSystemRoleInvitation()
    {
        // Given
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var defaultUserRole = await GetRoleAsync(organization.Id, Role.DefaultUserName);
        await SetSeatLimitAsync(organization.Id, 2);
        var client = apiFactory.CreateAuthenticatedClient(owner, Permission.AddUser);

        // When
        var response = await client.POSTAsync<InviteOrganizationMemberEndpoint, InviteOrganizationMemberRequest>(
            new InviteOrganizationMemberRequest
            {
                Email = "default-user-invite@example.com",
                RoleId = defaultUserRole.Id,
            });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var invitation = await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>()
            .OrganizationInvitations.SingleAsync(candidate =>
                candidate.OrganizationId == organization.Id
                && candidate.Email == "default-user-invite@example.com",
                TestContext.Current.CancellationToken);
        invitation.RoleId.ShouldBe(defaultUserRole.Id);
        invitation.RoleName.ShouldBe(Role.DefaultUserName);
    }

    [Fact]
    public async Task When_InvitationEmailDoesNotMatchAccount_Then_Returns403()
    {
        // Given
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var (otherUser, _, _) = await apiFactory.Services.SeedUserAsync();
        const string token = "email-mismatch-invitation-token";
        await SeedInvitationAsync(owner, organization, "different@example.com", token);
        var client = apiFactory.CreateAuthenticatedClient(otherUser);

        // When
        var response = await client.POSTAsync<AcceptOrganizationInvitationEndpoint, AcceptOrganizationInvitationRequest>(
            new AcceptOrganizationInvitationRequest { Token = token });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task When_UnverifiedUserAcceptsInvitation_Then_Returns403()
    {
        // Given
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var unverifiedUserFaker = UserFaker.Create().RuleFor(x => x.EmailVerified, false);
        var (invitedUser, _, _) = await apiFactory.Services.SeedUserAsync(unverifiedUserFaker);
        const string token = "unverified-user-organization-invitation-token";
        await SeedInvitationAsync(owner, organization, invitedUser.Email, token);
        var client = apiFactory.CreateAuthenticatedClient(invitedUser);

        // When
        var response = await client.POSTAsync<
            AcceptOrganizationInvitationEndpoint,
            AcceptOrganizationInvitationRequest>(new AcceptOrganizationInvitationRequest { Token = token });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task When_AcceptingInvitation_Then_AddsMembershipAndIssuesSessionForOrganization()
    {
        // Given
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var (invitedUser, _, _) = await apiFactory.Services.SeedUserAsync();
        await SetSeatLimitAsync(organization.Id, 2);
        const string token = "valid-organization-invitation-token";
        await SeedInvitationAsync(owner, organization, invitedUser.Email, token);
        var client = apiFactory.CreateAuthenticatedClient(invitedUser);

        // When
        var (acceptResponse, session) = await client.POSTAsync<
            AcceptOrganizationInvitationEndpoint,
            AcceptOrganizationInvitationRequest,
            Palladin.Module.Identity.Shared.AuthSessionResponse>(
            new AcceptOrganizationInvitationRequest { Token = token });

        // Then
        acceptResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        new JwtSecurityTokenHandler().ReadJwtToken(session.AccessToken).Claims
            .Single(c => c.Type == JwtClaimNames.OrganizationId).Value.ShouldBe(organization.Id.ToString());

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        (await readContext.OrganizationMembers.AnyAsync(
            m => m.OrganizationId == organization.Id && m.UserId == invitedUser.Id)).ShouldBeTrue();
    }

    [Fact]
    public async Task When_SeatLimitIsReachedBeforeAcceptance_Then_AcceptReturns409()
    {
        // Given
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var (invitedUser, _, _) = await apiFactory.Services.SeedUserAsync();
        const string token = "seat-limit-organization-invitation-token";
        await SeedInvitationAsync(owner, organization, invitedUser.Email, token);
        var client = apiFactory.CreateAuthenticatedClient(invitedUser);

        // When
        var response = await client.POSTAsync<
            AcceptOrganizationInvitationEndpoint,
            AcceptOrganizationInvitationRequest>(new AcceptOrganizationInvitationRequest { Token = token });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).ShouldContain("organization-seat-limit-reached");

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        (await readContext.OrganizationMembers.AnyAsync(
            member => member.OrganizationId == organization.Id && member.UserId == invitedUser.Id)).ShouldBeFalse();
    }

    [Fact]
    public async Task When_ChangingOwnerRole_Then_Returns409()
    {
        // Given
        var (owner, _, administratorRole) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(owner, Permission.OrganizationManagement);

        // When
        var response = await client.PUTAsync<
            UpdateOrganizationMemberRolesEndpoint,
            UpdateOrganizationMemberRolesRequest>(new UpdateOrganizationMemberRolesRequest
            {
                UserId = owner.Id,
                RoleIds = [administratorRole.Id],
            });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task When_AssigningMultipleRoles_Then_PersistsAllRolesAndCombinesPermissions()
    {
        // Given
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var member = await apiFactory.Services.SeedAdditionalOrganizationMemberAsync(organization.Id);
        var existingRole = await GetRoleAsync(organization.Id, "Test Member");
        var auditRole = await CreateRoleAsync(organization.Id, "Audit Reader", Permission.AuditView);
        var client = apiFactory.CreateAuthenticatedClient(owner, Permission.OrganizationManagement);

        // When
        var response = await client.PUTAsync<
            UpdateOrganizationMemberRolesEndpoint,
            UpdateOrganizationMemberRolesRequest>(new UpdateOrganizationMemberRolesRequest
            {
                UserId = member.Id,
                RoleIds = [existingRole.Id, auditRole.Id],
            });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        var persistedMember = await readContext.OrganizationMembers
            .Include(m => m.RoleAssignments)
                .ThenInclude(assignment => assignment.Role)
            .Where(m => m.OrganizationId == organization.Id && m.UserId == member.Id)
            .SingleAsync();
        persistedMember.RoleAssignments.Select(assignment => assignment.Role.Name)
            .ShouldBe(["Audit Reader", "Test Member"], ignoreOrder: true);
        persistedMember.EffectivePermissions().ShouldBe(Permission.AuditView | Permission.GrantManage);

        var memberClient = apiFactory.CreateAuthenticatedClient(member);
        var (switchResponse, session) = await memberClient.POSTAsync<
            SwitchOrganizationEndpoint,
            SwitchOrganizationRequest,
            Palladin.Module.Identity.Shared.AuthSessionResponse>(
            new SwitchOrganizationRequest { OrganizationId = organization.Id });
        switchResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        new JwtSecurityTokenHandler().ReadJwtToken(session.AccessToken).Claims
            .Single(claim => claim.Type == JwtClaimNames.Permissions).Value
            .ShouldBe(((int)(Permission.AuditView | Permission.GrantManage)).ToString());
    }

    [Fact]
    public async Task When_AssigningRoleFromAnotherOrganization_Then_Returns400()
    {
        // Given
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var member = await apiFactory.Services.SeedAdditionalOrganizationMemberAsync(organization.Id);
        var (_, otherOrganization, _) = await apiFactory.Services.SeedUserAsync();
        var foreignRole = await CreateRoleAsync(otherOrganization.Id, "Foreign Role", Permission.AuditView);
        var client = apiFactory.CreateAuthenticatedClient(owner, Permission.OrganizationManagement);

        // When
        var response = await client.PUTAsync<
            UpdateOrganizationMemberRolesEndpoint,
            UpdateOrganizationMemberRolesRequest>(new UpdateOrganizationMemberRolesRequest
            {
                UserId = member.Id,
                RoleIds = [foreignRole.Id],
            });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task When_ReplacingRolesWithoutGrantManageDelta_Then_RemovesUnselectedAssignments()
    {
        // Given
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var member = await apiFactory.Services.SeedAdditionalOrganizationMemberAsync(organization.Id);
        var replacementRole = await CreateRoleAsync(
            organization.Id,
            "Replacement Role",
            Permission.GrantManage | Permission.VaultManage);
        var client = apiFactory.CreateAuthenticatedClient(owner, Permission.OrganizationManagement);

        // When
        var response = await client.PUTAsync<
            UpdateOrganizationMemberRolesEndpoint,
            UpdateOrganizationMemberRolesRequest>(new UpdateOrganizationMemberRolesRequest
            {
                UserId = member.Id,
                RoleIds = [replacementRole.Id],
            });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        var assignedRoles = await readContext.OrganizationMemberRoles
            .Where(assignment => assignment.OrganizationId == organization.Id && assignment.UserId == member.Id)
            .Select(assignment => assignment.Role.Name)
            .ToListAsync();
        assignedRoles.ShouldBe(["Replacement Role"]);
    }

    [Fact]
    public async Task When_AcceptingInvitationTwice_Then_SecondAttemptReturns400()
    {
        // Given
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var (invitedUser, _, _) = await apiFactory.Services.SeedUserAsync();
        await SetSeatLimitAsync(organization.Id, 2);
        const string token = "single-use-organization-invitation-token";
        await SeedInvitationAsync(owner, organization, invitedUser.Email, token);
        var client = apiFactory.CreateAuthenticatedClient(invitedUser);
        var request = new AcceptOrganizationInvitationRequest { Token = token };

        // When
        var firstResponse = await client.POSTAsync<
            AcceptOrganizationInvitationEndpoint,
            AcceptOrganizationInvitationRequest>(request);
        var secondResponse = await client.POSTAsync<
            AcceptOrganizationInvitationEndpoint,
            AcceptOrganizationInvitationRequest>(request);

        // Then
        firstResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        secondResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task When_RemovedUserRejoins_Then_MembershipVersionsContinueBeyondHistoricalDispatch()
    {
        // Given
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var (invitedUser, _, _) = await apiFactory.Services.SeedUserAsync();
        await SetSeatLimitAsync(organization.Id, 2);
        const string token = "rejoin-with-monotonic-membership-versions";
        var invitation = await SeedInvitationAsync(owner, organization, invitedUser.Email, token);
        var now = apiFactory.FakeClock.GetCurrentInstant();
        await using (var seedScope = apiFactory.Services.CreateAsyncScope())
        {
            var writeContext = seedScope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
            writeContext.OrganizationMemberRoleSetDispatches.Add(OrganizationMemberRoleSetDispatch.Create(
                organization.Id,
                invitedUser.Id,
                [invitation.RoleId!.Value],
                revision: 7,
                authorizationVersion: 9,
                isActive: false,
                updatedAt: now));
            await writeContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var client = apiFactory.CreateAuthenticatedClient(invitedUser);

        // When
        var response = await client.POSTAsync<
            AcceptOrganizationInvitationEndpoint,
            AcceptOrganizationInvitationRequest>(new AcceptOrganizationInvitationRequest { Token = token });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        var membership = await readContext.OrganizationMembers.SingleAsync(x =>
            x.OrganizationId == organization.Id && x.UserId == invitedUser.Id);
        membership.AuthorizationVersion.ShouldBe(10u);
        membership.VaultAccessRevision.ShouldBe(8ul);
        var validator = scope.ServiceProvider.GetRequiredService<IOrganizationMembershipValidator>();
        (await validator.IsActiveAsync(
            invitedUser.Id, organization.Id, 9u, TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await validator.IsActiveAsync(
            invitedUser.Id, organization.Id, 10u, TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [Fact]
    public async Task When_InvitationExpired_Then_AcceptReturns400()
    {
        // Given
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var (invitedUser, _, _) = await apiFactory.Services.SeedUserAsync();
        const string token = "expired-organization-invitation-token";
        await SeedInvitationAsync(owner, organization, invitedUser.Email, token, Duration.FromHours(-1));
        var client = apiFactory.CreateAuthenticatedClient(invitedUser);

        // When
        var response = await client.POSTAsync<
            AcceptOrganizationInvitationEndpoint,
            AcceptOrganizationInvitationRequest>(new AcceptOrganizationInvitationRequest { Token = token });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task When_RemovingMember_Then_ReadsRemainCurrentButSwitchCannotIssueSession()
    {
        // Given
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var member = await apiFactory.Services.SeedAdditionalOrganizationMemberAsync(organization.Id);
        // Keep the staged removal open deterministically. Without an affected Vault the asynchronous
        // workflow may complete and delete the membership before the switch request, correctly
        // changing the response from inactive-membership 403 to missing-membership 401.
        await apiFactory.Services.SeedVaultAsync(organization.Id, member.Id);
        var client = apiFactory.CreateAuthenticatedClient(owner, Permission.OrganizationManagement);
        var removedMemberClient = apiFactory.CreateAuthenticatedClient(member);

        // When
        var response = await client.DELETEAsync<RemoveOrganizationMemberEndpoint, RemoveOrganizationMemberRequest>(
            new RemoveOrganizationMemberRequest { UserId = member.Id });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        var membership = await readContext.OrganizationMembers.SingleAsync(
            m => m.OrganizationId == organization.Id && m.UserId == member.Id);
        membership.Status.ShouldBe(OrganizationMemberStatus.Removing);
        membership.RemovalRequestId.ShouldNotBeNull();

        var stillAuthorizedResponse = await removedMemberClient.GetAsync("api/org");
        stillAuthorizedResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var switchResponse = await removedMemberClient.POSTAsync<
            SwitchOrganizationEndpoint,
            SwitchOrganizationRequest>(new SwitchOrganizationRequest { OrganizationId = organization.Id });
        switchResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await readContext.RefreshTokens.CountAsync(token =>
            token.OrganizationId == organization.Id && token.UserId == member.Id)).ShouldBe(0);
    }

    [Fact]
    public async Task When_AllVaultRotationsComplete_Then_MemberAndSessionsAreRemovedAtomically()
    {
        // Given
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var member = await apiFactory.Services.SeedAdditionalOrganizationMemberAsync(organization.Id);
        var tokenIds = Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToArray();
        await using (var tokenScope = apiFactory.Services.CreateAsyncScope())
        {
            var writeContext = tokenScope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
            var now = apiFactory.FakeClock.GetCurrentInstant();
            writeContext.RefreshTokens.AddRange(tokenIds.Select((id, index) => RefreshToken.Create(
                id,
                member.Id,
                organization.Id,
                $"member-removal-token-hash-{index}-{id}",
                authorizationVersion: 1,
                now + Duration.FromDays(30),
                now)));
            await writeContext.SaveChangesAsync();
        }
        var requestId = Guid.NewGuid();
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var writeContext = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
            var membership = await writeContext.OrganizationMembers.SingleAsync(
                x => x.OrganizationId == organization.Id && x.UserId == member.Id);
            membership.RequestRemoval(
                requestId,
                owner.Id,
                apiFactory.FakeClock.GetCurrentInstant());
            membership.FetchEvents();
            await writeContext.SaveChangesAsync();
        }

        // When
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var consumer = ActivatorUtilities.CreateInstance<OnOrganizationMemberRemovalCompleted>(scope.ServiceProvider);
            await consumer.Consume(apiFactory.MockConsumeContext(new OrganizationMemberRemovalCompletedEvent(
                requestId,
                organization.Id,
                member.Id,
                apiFactory.FakeClock.GetCurrentInstant(),
                apiFactory.FakeClock.GetCurrentInstant())));
        }

        // Then
        await using var verificationScope = apiFactory.Services.CreateAsyncScope();
        var readContext = verificationScope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        (await readContext.OrganizationMembers.AnyAsync(
            x => x.OrganizationId == organization.Id && x.UserId == member.Id)).ShouldBeFalse();
        (await readContext.RefreshTokens.CountAsync(x =>
            x.UserId == member.Id && tokenIds.Contains(x.Id) && x.RevokedAt != null)).ShouldBe(tokenIds.Length);
    }

    [Fact]
    public async Task When_StaleRemovalCompletionArrivesAfterReinvite_Then_ItIsIgnored()
    {
        // Given
        var (_, organization, _) = await apiFactory.Services.SeedUserAsync();
        var member = await apiFactory.Services.SeedAdditionalOrganizationMemberAsync(organization.Id);
        Instant currentUpdatedAt;
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            currentUpdatedAt = await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>()
                .OrganizationMembers
                .Where(x => x.OrganizationId == organization.Id && x.UserId == member.Id)
                .Select(x => x.UpdatedAt)
                .SingleAsync();
        }
        var staleCompletedAt = currentUpdatedAt - Duration.FromMinutes(1);

        // When
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var consumer = ActivatorUtilities.CreateInstance<OnOrganizationMemberRemovalCompleted>(scope.ServiceProvider);
            await consumer.Consume(apiFactory.MockConsumeContext(new OrganizationMemberRemovalCompletedEvent(
                Guid.NewGuid(),
                organization.Id,
                member.Id,
                staleCompletedAt,
                staleCompletedAt)));
        }

        // Then
        await using var verificationScope = apiFactory.Services.CreateAsyncScope();
        var persisted = await verificationScope.ServiceProvider.GetRequiredService<IdentityDbReadContext>()
            .OrganizationMembers.SingleAsync(
                x => x.OrganizationId == organization.Id && x.UserId == member.Id);
        persisted.Status.ShouldBe(OrganizationMemberStatus.Active);
        persisted.UpdatedAt.ShouldBe(currentUpdatedAt);
    }

    [Fact]
    public async Task When_UserLacksMemberManagementPermission_Then_InviteReturns403()
    {
        // Given
        var (user, _, role) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user, Permission.VaultCreate);

        // When
        var response = await client.POSTAsync<InviteOrganizationMemberEndpoint, InviteOrganizationMemberRequest>(
            new InviteOrganizationMemberRequest { Email = "invitee@example.com", RoleId = role.Id });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private async Task<OrganizationInvitation> SeedInvitationAsync(
        User owner,
        Organization organization,
        string email,
        string token,
        Duration? ttl = null,
        Instant? issuedAt = null)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
        var role = await writeContext.Roles.FirstOrDefaultAsync(
            r => r.OrganizationId == organization.Id && !r.IsSystem);
        if (role is null)
        {
            role = Role.Create(
                Guid.NewGuid(), organization.Id, "Invitation Member", Permission.None,
                isSystem: false, apiFactory.FakeClock.GetCurrentInstant());
            writeContext.Roles.Add(role);
        }
        var now = TruncateToMicroseconds(
            issuedAt ?? scope.ServiceProvider.GetRequiredService<IClock>().GetCurrentInstant());
        var tokenHash = TokenService.HashToken(token);

        await writeContext.OrganizationInvitations
            .Where(i => i.TokenHash == tokenHash)
            .ExecuteDeleteAsync();

        var invitation = OrganizationInvitation.Create(
            Guid.NewGuid(), organization.Id, organization.Name, role.Id, role.Name,
            owner.Id, owner.DisplayName, email, "en", token, tokenHash,
            ttl ?? Duration.FromHours(72), now);
        writeContext.OrganizationInvitations.Add(invitation);
        await writeContext.SaveChangesAsync();
        return invitation;
    }

    private async Task<Role> CreateRoleAsync(Guid organizationId, string name, Permission permissions)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
        var role = Role.Create(
            Guid.NewGuid(), organizationId, name, permissions,
            isSystem: false, SystemClock.Instance.GetCurrentInstant());
        writeContext.Roles.Add(role);
        await writeContext.SaveChangesAsync();
        return role;
    }

    private async Task<Role> GetRoleAsync(Guid organizationId, string name)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        return await readContext.Roles.SingleAsync(role =>
            role.OrganizationId == organizationId && role.Name == name);
    }

    private async Task SetSeatLimitAsync(Guid organizationId, int seatLimit)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
        await writeContext.Organizations
            .Where(organization => organization.Id == organizationId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                organization => organization.SeatLimit, seatLimit));
    }

    private static Instant TruncateToMicroseconds(Instant value)
    {
        var ticks = value.ToUnixTimeTicks();
        return Instant.FromUnixTimeTicks(ticks - ticks % 10);
    }
}
