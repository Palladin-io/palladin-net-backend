using System.IdentityModel.Tokens.Jwt;
using System.Net;
using Bogus;
using Palladin.Core.Security;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Features;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.Persistence;
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
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Identity;

[Collection<ApiFactoryCollection>]
public sealed class OrganizationMembershipTests(ApiFactory apiFactory) : TestBase
{
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
        var (user, organization, role) = await apiFactory.Services.SeedUserAsync();
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
        var (owner, organization, role) = await apiFactory.Services.SeedUserAsync();
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
        var (owner, organization, role) = await apiFactory.Services.SeedUserAsync();
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
    public async Task When_ConcurrentInvitesCompeteForLastSeat_Then_OnlyOneIsCreated()
    {
        // Given
        var (owner, organization, role) = await apiFactory.Services.SeedUserAsync();
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
        result.Items.Count.ShouldBe(2);
        result.Items.Single(role => role.Id == administratorRole.Id).Permissions.ShouldBe(int.MaxValue);
        result.Items.Single(role => role.Id == customRole.Id).Permissions.ShouldBe((int)Permission.AuditView);
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
    public async Task When_AcceptingInvitation_Then_AddsMembershipAndAllowsOrganizationSwitch()
    {
        // Given
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var (invitedUser, _, _) = await apiFactory.Services.SeedUserAsync();
        await SetSeatLimitAsync(organization.Id, 2);
        const string token = "valid-organization-invitation-token";
        await SeedInvitationAsync(owner, organization, invitedUser.Email, token);
        var client = apiFactory.CreateAuthenticatedClient(invitedUser);

        // When
        var acceptResponse = await client.POSTAsync<
            AcceptOrganizationInvitationEndpoint,
            AcceptOrganizationInvitationRequest>(new AcceptOrganizationInvitationRequest { Token = token });
        var (switchResponse, session) = await client.POSTAsync<
            SwitchOrganizationEndpoint,
            SwitchOrganizationRequest,
            Palladin.Module.Identity.Shared.AuthSessionResponse>(
            new SwitchOrganizationRequest { OrganizationId = organization.Id });

        // Then
        acceptResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        switchResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
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
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
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
    public async Task When_ReplacingRoles_Then_RemovesUnselectedAssignments()
    {
        // Given
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var member = await apiFactory.Services.SeedAdditionalOrganizationMemberAsync(organization.Id);
        var replacementRole = await CreateRoleAsync(organization.Id, "Replacement Role", Permission.VaultManage);
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
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
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
        firstResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        secondResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
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
    public async Task When_RemovingMember_Then_MembershipRemainsEffectiveUntilVaultSagaCompletes()
    {
        // Given
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var member = await apiFactory.Services.SeedAdditionalOrganizationMemberAsync(organization.Id);
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

    private async Task SeedInvitationAsync(
        User owner,
        Organization organization,
        string email,
        string token,
        Duration? ttl = null)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
        var role = await writeContext.Roles.FirstAsync(r => r.OrganizationId == organization.Id);
        var now = scope.ServiceProvider.GetRequiredService<IClock>().GetCurrentInstant();
        var tokenHash = TokenService.HashToken(token);

        await writeContext.OrganizationInvitations
            .Where(i => i.TokenHash == tokenHash)
            .ExecuteDeleteAsync();

        writeContext.OrganizationInvitations.Add(OrganizationInvitation.Create(
            Guid.NewGuid(), organization.Id, organization.Name, role.Id, role.Name,
            owner.Id, owner.DisplayName, email, "en", token, tokenHash,
            ttl ?? Duration.FromHours(72), now));
        await writeContext.SaveChangesAsync();
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
}
