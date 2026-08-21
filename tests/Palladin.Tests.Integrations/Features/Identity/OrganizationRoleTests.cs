using System.Net;
using System.Net.Http.Json;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Palladin.Core.Security;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Features;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Shared;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Identity;

[Collection<ApiFactoryCollection>]
public sealed class OrganizationRoleTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_ListingRoles_Then_ReturnsAssignmentCountsAndDefinedPermissionSet()
    {
        // Given
        var (owner, organization, administratorRole) = await apiFactory.Services.SeedUserAsync();
        var member = await apiFactory.Services.SeedAdditionalOrganizationMemberAsync(organization.Id);
        var client = apiFactory.CreateAuthenticatedClient(owner, Permission.OrganizationManagement);

        // When
        var (response, result) = await client.GETAsync<
            ListOrganizationRolesEndpoint,
            ListOrganizationRolesResponse>();

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.Items.Single(x => x.Id == administratorRole.Id).AssignedMemberCount.ShouldBe(1);
        result.Items.Single(x => x.Id == administratorRole.Id).CanAssign.ShouldBeTrue();
        var defaultUserRole = result.Items.Single(x => x.Name == Role.DefaultUserName);
        defaultUserRole.Permissions.ShouldBe((int)Role.DefaultUserPermissions);
        defaultUserRole.IsSystem.ShouldBeTrue();
        defaultUserRole.AssignedMemberCount.ShouldBe(0);
        defaultUserRole.CanAssign.ShouldBeTrue();
        result.Items.Single(x => x.Name == "Test Member").AssignedMemberCount.ShouldBe(1);
        result.AssignablePermissions.Select(x => x.Value).ShouldBe(
            new[] { 1, 2, 4, 8, 16, 32, 64, 128, 4096, 8192 },
            ignoreOrder: false);
        result.AssignablePermissions
            .All(x => x.Value != 256 && x.Value != 512 && x.Value != int.MaxValue)
            .ShouldBeTrue();
        result.AssignablePermissions.ShouldAllBe(permission => permission.CanAssign);
        member.Id.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task When_InviterLacksOrganizationManagement_Then_UsesMinimalInvitationRoleList()
    {
        // Given
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var invitationRole = await CreateRoleAsync(organization.Id, "Invited Member", Permission.None);
        var client = apiFactory.CreateAuthenticatedClient(owner, Permission.AddUser);

        // When
        var managementResponse = await client.GetAsync(
            "api/organization/roles", TestContext.Current.CancellationToken);
        var (invitationResponse, invitationRoles) = await client.GETAsync<
            ListOrganizationInvitationRolesEndpoint,
            ListOrganizationInvitationRolesResponse>();

        // Then
        managementResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        invitationResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        invitationRoles.Items.Select(role => role.Id).ShouldBe(
            [invitationRole.Id, (await GetRoleAsync(organization.Id, Role.DefaultUserName)).Id],
            ignoreOrder: true);
    }

    [Fact]
    public async Task When_DelegatingDefaultUserRole_Then_RequiresItsVaultPermissionCeiling()
    {
        // Given
        var (_, organization, _) = await apiFactory.Services.SeedUserAsync();
        var defaultUserRole = await GetRoleAsync(organization.Id, Role.DefaultUserName);
        var authorizedRole = await CreateRoleAsync(
            organization.Id,
            "User Inviter",
            Permission.AddUser | Permission.VaultCreate | Permission.VaultManage);
        var authorizedInviter = await SeedMemberWithRoleAsync(organization.Id, authorizedRole);
        var unauthorizedRole = await CreateRoleAsync(
            organization.Id,
            "Limited Inviter",
            Permission.AddUser | Permission.VaultCreate);
        var unauthorizedInviter = await SeedMemberWithRoleAsync(organization.Id, unauthorizedRole);

        var authorizedClient = apiFactory.CreateAuthenticatedClient(
            authorizedInviter,
            Permission.AddUser | Permission.VaultCreate | Permission.VaultManage);
        var unauthorizedClient = apiFactory.CreateAuthenticatedClient(
            unauthorizedInviter,
            Permission.AddUser | Permission.VaultCreate);

        // When
        var (_, authorizedRoles) = await authorizedClient.GETAsync<
            ListOrganizationInvitationRolesEndpoint,
            ListOrganizationInvitationRolesResponse>();
        var (_, unauthorizedRoles) = await unauthorizedClient.GETAsync<
            ListOrganizationInvitationRolesEndpoint,
            ListOrganizationInvitationRolesResponse>();

        // Then
        authorizedRoles.Items.Select(role => role.Id).ShouldContain(defaultUserRole.Id);
        unauthorizedRoles.Items.Select(role => role.Id).ShouldNotContain(defaultUserRole.Id);
    }

    [Fact]
    public async Task When_CreatingRole_Then_NameIsNormalizedAndCaseInsensitiveDuplicateIsRejected()
    {
        // Given
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(owner, Permission.OrganizationManagement);

        // When
        var (createdResponse, created) = await client.POSTAsync<
            CreateOrganizationRoleEndpoint,
            CreateOrganizationRoleRequest,
            OrganizationRoleItem>(new CreateOrganizationRoleRequest
            {
                Name = "  Security Auditor  ",
                Permissions = (int)Permission.AuditView,
            });
        var duplicateResponse = await client.POSTAsync<
            CreateOrganizationRoleEndpoint,
            CreateOrganizationRoleRequest>(new CreateOrganizationRoleRequest
            {
                Name = "security auditor",
                Permissions = (int)Permission.AuditView,
            });

        // Then
        createdResponse.StatusCode.ShouldBe(HttpStatusCode.Created);
        created.Name.ShouldBe("Security Auditor");
        created.AssignedMemberCount.ShouldBe(0);
        created.CanAssign.ShouldBeTrue();
        duplicateResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await duplicateResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldContain("organization-role-name-conflict");

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var role = await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>()
            .Roles.SingleAsync(
                x => x.OrganizationId == organization.Id && x.Id == created.Id,
                TestContext.Current.CancellationToken);
        role.NormalizedName.ShouldBe("SECURITY AUDITOR");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(256)]
    [InlineData(512)]
    [InlineData(int.MaxValue)]
    public async Task When_CreatingRoleWithUnsupportedPermissions_Then_Returns400(int permissions)
    {
        // Given
        var (owner, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(owner, Permission.OrganizationManagement);

        // When
        var response = await client.POSTAsync<CreateOrganizationRoleEndpoint, CreateOrganizationRoleRequest>(
            new CreateOrganizationRoleRequest { Name = "Invalid permissions", Permissions = permissions });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task When_CreatingRoleWithWhitespaceName_Then_Returns400()
    {
        // Given
        var (owner, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(owner, Permission.OrganizationManagement);

        // When
        var response = await client.POSTAsync<CreateOrganizationRoleEndpoint, CreateOrganizationRoleRequest>(
            new CreateOrganizationRoleRequest { Name = "   ", Permissions = (int)Permission.AuditView });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task When_MutatingAdministratorRole_Then_Returns409AndKeepsRole()
    {
        // Given
        var (owner, organization, administratorRole) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(owner, Permission.OrganizationManagement);

        // When
        var updateResponse = await client.PUTAsync<UpdateOrganizationRoleEndpoint, UpdateOrganizationRoleRequest>(
            new UpdateOrganizationRoleRequest
            {
                RoleId = administratorRole.Id,
                Name = "Changed",
                Permissions = (int)Permission.AuditView,
            });
        var deleteResponse = await client.DELETEAsync<DeleteOrganizationRoleEndpoint, DeleteOrganizationRoleRequest>(
            new DeleteOrganizationRoleRequest { RoleId = administratorRole.Id });

        // Then
        updateResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        deleteResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var persisted = await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>()
            .Roles.SingleAsync(
                x => x.OrganizationId == organization.Id && x.Id == administratorRole.Id,
                TestContext.Current.CancellationToken);
        persisted.Name.ShouldBe("Administrator");
        persisted.Permissions.ShouldBe((Permission)int.MaxValue);
    }

    [Fact]
    public async Task When_MutatingDefaultUserRole_Then_Returns409AndKeepsRole()
    {
        // Given
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var defaultUserRole = await GetRoleAsync(organization.Id, Role.DefaultUserName);
        var client = apiFactory.CreateAuthenticatedClient(owner, Permission.OrganizationManagement);

        // When
        var updateResponse = await client.PUTAsync<UpdateOrganizationRoleEndpoint, UpdateOrganizationRoleRequest>(
            new UpdateOrganizationRoleRequest
            {
                RoleId = defaultUserRole.Id,
                Name = "Changed",
                Permissions = (int)Permission.AuditView,
            });
        var deleteResponse = await client.DELETEAsync<DeleteOrganizationRoleEndpoint, DeleteOrganizationRoleRequest>(
            new DeleteOrganizationRoleRequest { RoleId = defaultUserRole.Id });

        // Then
        updateResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        deleteResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var persisted = await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>()
            .Roles.SingleAsync(
                role => role.OrganizationId == organization.Id && role.Id == defaultUserRole.Id,
                TestContext.Current.CancellationToken);
        persisted.Name.ShouldBe(Role.DefaultUserName);
        persisted.Permissions.ShouldBe(Role.DefaultUserPermissions);
        persisted.IsSystem.ShouldBeTrue();
    }

    [Fact]
    public async Task When_UpdatingAssignedRoleWithoutGrantManageDelta_Then_InvalidatesAccessAndRefreshSessions()
    {
        // Given
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var member = await apiFactory.Services.SeedAdditionalOrganizationMemberAsync(organization.Id);
        var role = await GetRoleAsync(organization.Id, "Test Member");
        var oldAccessClient = apiFactory.CreateAuthenticatedClient(member);
        var rawRefreshToken = Convert.ToBase64String(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray());
        await apiFactory.Services.SeedRefreshTokenAsync(member.Id, rawRefreshToken);
        var client = apiFactory.CreateAuthenticatedClient(owner, Permission.OrganizationManagement);

        // When
        var (response, result) = await client.PUTAsync<
            UpdateOrganizationRoleEndpoint,
            UpdateOrganizationRoleRequest,
            OrganizationRoleItem>(new UpdateOrganizationRoleRequest
            {
                RoleId = role.Id,
                Name = role.Name,
                Permissions = (int)(Permission.GrantManage | Permission.AuditView),
            });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.Permissions.ShouldBe((int)(Permission.GrantManage | Permission.AuditView));
        result.CanAssign.ShouldBeTrue();
        (await oldAccessClient.GetAsync("api/org", TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var refreshResponse = await apiFactory.CreateClient().PostAsJsonAsync(
            "api/auth/refresh",
            new { RefreshToken = rawRefreshToken },
            TestContext.Current.CancellationToken);
        refreshResponse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var membership = await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>()
            .OrganizationMembers.SingleAsync(
                x => x.OrganizationId == organization.Id && x.UserId == member.Id,
                TestContext.Current.CancellationToken);
        membership.AuthorizationVersion.ShouldBe(2u);
    }

    [Fact]
    public async Task When_RoleUpdateIsNoOp_Then_KeepsAuthorizationSessionsCurrent()
    {
        // Given
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var member = await apiFactory.Services.SeedAdditionalOrganizationMemberAsync(organization.Id);
        var role = await GetRoleAsync(organization.Id, "Test Member");
        var oldAccessClient = apiFactory.CreateAuthenticatedClient(member);
        var rawRefreshToken = Convert.ToBase64String(Enumerable.Range(65, 32).Select(x => (byte)x).ToArray());
        var refreshToken = await apiFactory.Services.SeedRefreshTokenAsync(member.Id, rawRefreshToken);
        var client = apiFactory.CreateAuthenticatedClient(owner, Permission.OrganizationManagement);

        // When
        var response = await client.PUTAsync<UpdateOrganizationRoleEndpoint, UpdateOrganizationRoleRequest>(
            new UpdateOrganizationRoleRequest
            {
                RoleId = role.Id,
                Name = role.Name,
                Permissions = (int)role.Permissions,
            });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await oldAccessClient.GetAsync("api/org", TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        (await readContext.OrganizationMembers.SingleAsync(
            x => x.OrganizationId == organization.Id && x.UserId == member.Id,
            TestContext.Current.CancellationToken))
            .AuthorizationVersion.ShouldBe(1u);
        (await readContext.RefreshTokens.SingleAsync(
            x => x.Id == refreshToken.Id, TestContext.Current.CancellationToken)).RevokedAt.ShouldBeNull();
    }

    [Fact]
    public async Task When_RoleUpdateChangesEffectiveGrantManage_Then_FailsClosedWithoutMutation()
    {
        // Given
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var member = await apiFactory.Services.SeedAdditionalOrganizationMemberAsync(organization.Id);
        var role = await GetRoleAsync(organization.Id, "Test Member");
        var oldAccessClient = apiFactory.CreateAuthenticatedClient(member);
        var rawRefreshToken = Convert.ToBase64String(Enumerable.Range(33, 32).Select(x => (byte)x).ToArray());
        var refreshToken = await apiFactory.Services.SeedRefreshTokenAsync(member.Id, rawRefreshToken);
        var client = apiFactory.CreateAuthenticatedClient(owner, Permission.OrganizationManagement);

        // When
        var response = await client.PUTAsync<UpdateOrganizationRoleEndpoint, UpdateOrganizationRoleRequest>(
            new UpdateOrganizationRoleRequest
            {
                RoleId = role.Id,
                Name = "No longer grants",
                Permissions = (int)Permission.AuditView,
            });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldContain("organization-role-grant-manage-cutover-unavailable");
        (await oldAccessClient.GetAsync("api/org", TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        var persistedRole = await readContext.Roles.SingleAsync(
            x => x.OrganizationId == organization.Id && x.Id == role.Id,
            TestContext.Current.CancellationToken);
        persistedRole.Name.ShouldBe("Test Member");
        persistedRole.Permissions.ShouldBe(Permission.GrantManage);
        (await readContext.OrganizationMembers.SingleAsync(
            x => x.OrganizationId == organization.Id && x.UserId == member.Id,
            TestContext.Current.CancellationToken))
            .AuthorizationVersion.ShouldBe(1u);
        (await readContext.RefreshTokens.SingleAsync(
            x => x.Id == refreshToken.Id, TestContext.Current.CancellationToken)).RevokedAt.ShouldBeNull();
    }

    [Fact]
    public async Task When_MemberRoleReplacementChangesEffectiveGrantManage_Then_FailsClosedWithoutMutation()
    {
        // Given
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var member = await apiFactory.Services.SeedAdditionalOrganizationMemberAsync(organization.Id);
        var auditRole = await CreateRoleAsync(organization.Id, "Auditor", Permission.AuditView);
        var client = apiFactory.CreateAuthenticatedClient(owner, Permission.OrganizationManagement);

        // When
        var response = await client.PUTAsync<
            UpdateOrganizationMemberRolesEndpoint,
            UpdateOrganizationMemberRolesRequest>(new UpdateOrganizationMemberRolesRequest
            {
                UserId = member.Id,
                RoleIds = [auditRole.Id],
            });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldContain("organization-role-grant-manage-cutover-unavailable");
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var persisted = await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>()
            .OrganizationMembers
            .Include(x => x.RoleAssignments)
                .ThenInclude(x => x.Role)
            .SingleAsync(
                x => x.OrganizationId == organization.Id && x.UserId == member.Id,
                TestContext.Current.CancellationToken);
        persisted.RoleAssignments.ShouldHaveSingleItem().Role.Name.ShouldBe("Test Member");
        persisted.AuthorizationVersion.ShouldBe(1u);
    }

    [Fact]
    public async Task When_ClearingAllRoles_Then_Returns400WithoutMutation()
    {
        // Given
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var auditRole = await CreateRoleAsync(organization.Id, "Auditor", Permission.AuditView);
        var member = await SeedMemberWithRoleAsync(organization.Id, auditRole);
        var client = apiFactory.CreateAuthenticatedClient(owner, Permission.OrganizationManagement);

        // When
        var response = await client.PUTAsync<
            UpdateOrganizationMemberRolesEndpoint,
            UpdateOrganizationMemberRolesRequest>(new UpdateOrganizationMemberRolesRequest
            {
                UserId = member.Id,
                RoleIds = [],
            });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var persisted = await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>()
            .OrganizationMembers
            .Include(candidate => candidate.RoleAssignments)
            .SingleAsync(candidate => candidate.OrganizationId == organization.Id && candidate.UserId == member.Id);
        persisted.RoleAssignments.ShouldHaveSingleItem().RoleId.ShouldBe(auditRole.Id);
        persisted.AuthorizationVersion.ShouldBe(1u);
    }

    [Fact]
    public async Task When_DeletingRole_Then_AllowsUnusedAndRejectsAssignedRole()
    {
        // Given
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        await apiFactory.Services.SeedAdditionalOrganizationMemberAsync(organization.Id);
        var assignedRole = await GetRoleAsync(organization.Id, "Test Member");
        var unusedRole = await CreateRoleAsync(organization.Id, "Unused", Permission.None);
        var client = apiFactory.CreateAuthenticatedClient(owner, Permission.OrganizationManagement);

        // When
        var unusedResponse = await client.DELETEAsync<DeleteOrganizationRoleEndpoint, DeleteOrganizationRoleRequest>(
            new DeleteOrganizationRoleRequest { RoleId = unusedRole.Id });
        var assignedResponse = await client.DELETEAsync<DeleteOrganizationRoleEndpoint, DeleteOrganizationRoleRequest>(
            new DeleteOrganizationRoleRequest { RoleId = assignedRole.Id });

        // Then
        unusedResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        assignedResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await assignedResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldContain("organization-role-in-use");
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var roles = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>().Roles;
        (await roles.AnyAsync(
            x => x.OrganizationId == organization.Id && x.Id == unusedRole.Id,
            TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await roles.AnyAsync(
            x => x.OrganizationId == organization.Id && x.Id == assignedRole.Id,
            TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [Fact]
    public async Task When_UpdatingRoleFromAnotherOrganization_Then_Returns404()
    {
        // Given
        var (owner, _, _) = await apiFactory.Services.SeedUserAsync();
        var (_, otherOrganization, _) = await apiFactory.Services.SeedUserAsync();
        var foreignRole = await CreateRoleAsync(otherOrganization.Id, "Foreign", Permission.AuditView);
        var client = apiFactory.CreateAuthenticatedClient(owner, Permission.OrganizationManagement);

        // When
        var response = await client.PUTAsync<UpdateOrganizationRoleEndpoint, UpdateOrganizationRoleRequest>(
            new UpdateOrganizationRoleRequest
            {
                RoleId = foreignRole.Id,
                Name = "Cross tenant",
                Permissions = (int)Permission.AuditView,
            });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task When_NonOwnerAttemptsIndirectOrSelfEscalation_Then_AllMutationSurfacesReturn403()
    {
        var (owner, organization, administratorRole) = await apiFactory.Services.SeedUserAsync();
        var actorRole = await CreateRoleAsync(
            organization.Id,
            "Role Manager",
            Permission.OrganizationManagement | Permission.AddUser | Permission.AuditView);
        var actor = await SeedMemberWithRoleAsync(organization.Id, actorRole);
        var billingRole = await CreateRoleAsync(organization.Id, "Billing", Permission.BillingManage);
        var client = apiFactory.CreateAuthenticatedClient(
            actor,
            Permission.OrganizationManagement | Permission.AddUser | Permission.AuditView);

        var (_, roleCatalog) = await client.GETAsync<
            ListOrganizationRolesEndpoint,
            ListOrganizationRolesResponse>();

        var createResponse = await client.POSTAsync<
            CreateOrganizationRoleEndpoint,
            CreateOrganizationRoleRequest>(new CreateOrganizationRoleRequest
            {
                Name = "Escalation storage",
                Permissions = (int)Permission.BillingManage,
            });
        var billingResponse = await client.PUTAsync<
            UpdateOrganizationMemberRolesEndpoint,
            UpdateOrganizationMemberRolesRequest>(new UpdateOrganizationMemberRolesRequest
            {
                UserId = actor.Id,
                RoleIds = [billingRole.Id],
            });
        var administratorResponse = await client.PUTAsync<
            UpdateOrganizationMemberRolesEndpoint,
            UpdateOrganizationMemberRolesRequest>(new UpdateOrganizationMemberRolesRequest
            {
                UserId = actor.Id,
                RoleIds = [administratorRole.Id],
            });

        createResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        billingResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        administratorResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        roleCatalog.Items.Single(role => role.Id == administratorRole.Id).CanAssign.ShouldBeFalse();
        roleCatalog.Items.Single(role => role.Id == billingRole.Id).CanAssign.ShouldBeFalse();
        roleCatalog.Items.Single(role => role.Id == actorRole.Id).CanAssign.ShouldBeTrue();
        roleCatalog.AssignablePermissions.Single(permission =>
            permission.Value == (int)Permission.BillingManage).CanAssign.ShouldBeFalse();
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        (await readContext.Roles.AnyAsync(role =>
            role.OrganizationId == organization.Id && role.Name == "Escalation storage")).ShouldBeFalse();
        var persistedActor = await readContext.OrganizationMembers
            .Include(member => member.RoleAssignments)
                .ThenInclude(assignment => assignment.Role)
            .SingleAsync(member => member.OrganizationId == organization.Id && member.UserId == actor.Id);
        persistedActor.RoleAssignments.ShouldHaveSingleItem().Role.Id.ShouldBe(actorRole.Id);
    }

    [Fact]
    public async Task When_NonOwnerAttemptsToManageHigherRoleOrPeer_Then_UpdateReturns403WithoutMutation()
    {
        var (_, organization, _) = await apiFactory.Services.SeedUserAsync();
        var actorRole = await CreateRoleAsync(
            organization.Id, "Role Manager", Permission.OrganizationManagement | Permission.AuditView);
        var actor = await SeedMemberWithRoleAsync(organization.Id, actorRole);
        var sharedRole = await CreateRoleAsync(organization.Id, "Shared Audit", Permission.AuditView);
        var billingRole = await CreateRoleAsync(organization.Id, "Higher Billing", Permission.BillingManage);
        await SeedMemberWithRolesAsync(organization.Id, [sharedRole, billingRole]);
        var client = apiFactory.CreateAuthenticatedClient(
            actor, Permission.OrganizationManagement | Permission.AuditView);

        var higherRoleResponse = await client.PUTAsync<
            UpdateOrganizationRoleEndpoint,
            UpdateOrganizationRoleRequest>(new UpdateOrganizationRoleRequest
            {
                RoleId = billingRole.Id,
                Name = billingRole.Name,
                Permissions = (int)Permission.None,
            });
        var higherPeerResponse = await client.PUTAsync<
            UpdateOrganizationRoleEndpoint,
            UpdateOrganizationRoleRequest>(new UpdateOrganizationRoleRequest
            {
                RoleId = sharedRole.Id,
                Name = sharedRole.Name,
                Permissions = (int)Permission.None,
            });

        higherRoleResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        higherPeerResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var roles = await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>()
            .Roles.Where(role => role.Id == sharedRole.Id || role.Id == billingRole.Id)
            .ToListAsync();
        roles.Single(role => role.Id == sharedRole.Id).Permissions.ShouldBe(Permission.AuditView);
        roles.Single(role => role.Id == billingRole.Id).Permissions.ShouldBe(Permission.BillingManage);
    }

    [Fact]
    public async Task When_NonOwnerListsAndInvitesRoles_Then_OnlyDelegableNonGrantManageRolesAreAvailable()
    {
        var (_, organization, administratorRole) = await apiFactory.Services.SeedUserAsync();
        var actorRole = await CreateRoleAsync(
            organization.Id, "Inviter", Permission.AddUser | Permission.AuditView);
        var actor = await SeedMemberWithRoleAsync(organization.Id, actorRole);
        var safeRole = await CreateRoleAsync(organization.Id, "Safe Audit", Permission.AuditView);
        var elevatedRole = await CreateRoleAsync(organization.Id, "Billing", Permission.BillingManage);
        var grantManageRole = await CreateRoleAsync(organization.Id, "Grant Manager", Permission.GrantManage);
        var client = apiFactory.CreateAuthenticatedClient(actor, Permission.AddUser | Permission.AuditView);

        var (_, selector) = await client.GETAsync<
            ListOrganizationInvitationRolesEndpoint,
            ListOrganizationInvitationRolesResponse>();
        var elevatedResponse = await client.POSTAsync<
            InviteOrganizationMemberEndpoint,
            InviteOrganizationMemberRequest>(new InviteOrganizationMemberRequest
            {
                Email = "elevated-invite@example.com",
                RoleId = elevatedRole.Id,
            });
        var administratorResponse = await client.POSTAsync<
            InviteOrganizationMemberEndpoint,
            InviteOrganizationMemberRequest>(new InviteOrganizationMemberRequest
            {
                Email = "administrator-invite@example.com",
                RoleId = administratorRole.Id,
            });
        var grantManageResponse = await client.POSTAsync<
            InviteOrganizationMemberEndpoint,
            InviteOrganizationMemberRequest>(new InviteOrganizationMemberRequest
            {
                Email = "grant-manager-invite@example.com",
                RoleId = grantManageRole.Id,
            });

        selector.Items.Select(role => role.Id).ShouldContain(safeRole.Id);
        selector.Items.Select(role => role.Id).ShouldNotContain(administratorRole.Id);
        selector.Items.Select(role => role.Id).ShouldNotContain(elevatedRole.Id);
        selector.Items.Select(role => role.Id).ShouldNotContain(grantManageRole.Id);
        elevatedResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        administratorResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        grantManageResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>()
            .OrganizationInvitations.AnyAsync(invitation =>
                invitation.OrganizationId == organization.Id)).ShouldBeFalse();
    }

    [Fact]
    public async Task When_OwnerAttemptsAdministratorInvitation_Then_GrantManageCutoverRemainsFailClosed()
    {
        // Given
        var (owner, organization, administratorRole) = await apiFactory.Services.SeedUserAsync();
        var (invitedUser, _, _) = await apiFactory.Services.SeedUserAsync();
        var ownerClient = apiFactory.CreateAuthenticatedClient(owner, Permission.AddUser);

        // When
        var (_, selector) = await ownerClient.GETAsync<
            ListOrganizationInvitationRolesEndpoint,
            ListOrganizationInvitationRolesResponse>();
        var inviteResponse = await ownerClient.POSTAsync<
            InviteOrganizationMemberEndpoint,
            InviteOrganizationMemberRequest>(new InviteOrganizationMemberRequest
            {
                Email = "administrator-owner-invite@example.com",
                RoleId = administratorRole.Id,
            });

        // Then
        selector.Items.Select(role => role.Id).ShouldNotContain(administratorRole.Id);
        inviteResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var legacyToken = $"legacy-administrator-invitation-{Guid.NewGuid():N}";
        var legacyTokenHash = Palladin.Module.Identity.Infrastructure.Jwt.TokenService.HashToken(legacyToken);
        await using (var seedScope = apiFactory.Services.CreateAsyncScope())
        {
            var writeContext = seedScope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
            writeContext.OrganizationInvitations.Add(OrganizationInvitation.Create(
                Guid.NewGuid(), organization.Id, organization.Name,
                administratorRole.Id, administratorRole.Name,
                owner.Id, owner.DisplayName, invitedUser.Email, "en",
                legacyToken, legacyTokenHash, Duration.FromHours(72),
                apiFactory.FakeClock.GetCurrentInstant()));
            await writeContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var acceptResponse = await apiFactory.CreateAuthenticatedClient(invitedUser).POSTAsync<
            AcceptOrganizationInvitationEndpoint,
            AcceptOrganizationInvitationRequest>(new AcceptOrganizationInvitationRequest { Token = legacyToken });

        acceptResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await acceptResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldContain("organization-role-grant-manage-cutover-unavailable");
        await using var verificationScope = apiFactory.Services.CreateAsyncScope();
        var readContext = verificationScope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        (await readContext.OrganizationInvitations.SingleAsync(
            invitation => invitation.TokenHash == legacyTokenHash,
            TestContext.Current.CancellationToken)).AcceptedAt.ShouldBeNull();
        (await readContext.OrganizationMembers.AnyAsync(member =>
            member.OrganizationId == organization.Id && member.UserId == invitedUser.Id,
            TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task When_ActiveInvitationRoleGainsGrantManage_Then_AcceptFailsClosedWithoutMutation()
    {
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var (invitedUser, _, _) = await apiFactory.Services.SeedUserAsync();
        var role = await CreateRoleAsync(organization.Id, "Pending Invite Role", Permission.AuditView);
        var token = $"grant-manage-cutover-{Guid.NewGuid():N}";
        var tokenHash = Palladin.Module.Identity.Infrastructure.Jwt.TokenService.HashToken(token);
        await using (var seedScope = apiFactory.Services.CreateAsyncScope())
        {
            var writeContext = seedScope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
            writeContext.OrganizationInvitations.Add(OrganizationInvitation.Create(
                Guid.NewGuid(), organization.Id, organization.Name, role.Id, role.Name,
                owner.Id, owner.DisplayName, invitedUser.Email, "en", token, tokenHash,
                Duration.FromHours(72), apiFactory.FakeClock.GetCurrentInstant()));
            await writeContext.SaveChangesAsync();
        }

        var ownerClient = apiFactory.CreateAuthenticatedClient(owner, Permission.OrganizationManagement);
        var updateResponse = await ownerClient.PUTAsync<
            UpdateOrganizationRoleEndpoint,
            UpdateOrganizationRoleRequest>(new UpdateOrganizationRoleRequest
            {
                RoleId = role.Id,
                Name = role.Name,
                Permissions = (int)Permission.GrantManage,
            });
        var invitedClient = apiFactory.CreateAuthenticatedClient(invitedUser);
        var acceptResponse = await invitedClient.POSTAsync<
            AcceptOrganizationInvitationEndpoint,
            AcceptOrganizationInvitationRequest>(new AcceptOrganizationInvitationRequest { Token = token });

        updateResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        acceptResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await acceptResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldContain("organization-role-grant-manage-cutover-unavailable");
        await using var verificationScope = apiFactory.Services.CreateAsyncScope();
        var readContext = verificationScope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        (await readContext.OrganizationInvitations.SingleAsync(invitation => invitation.TokenHash == tokenHash))
            .AcceptedAt.ShouldBeNull();
        (await readContext.OrganizationMembers.AnyAsync(member =>
            member.OrganizationId == organization.Id && member.UserId == invitedUser.Id)).ShouldBeFalse();
    }

    [Fact]
    public async Task When_InviterLosesAddUser_Then_AcceptFailsClosedWithoutMutation()
    {
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var (invitedUser, _, _) = await apiFactory.Services.SeedUserAsync();
        var inviterRole = await CreateRoleAsync(
            organization.Id, "Inviter", Permission.AddUser | Permission.AuditView);
        var inviter = await SeedMemberWithRoleAsync(organization.Id, inviterRole);
        var invitationRole = await CreateRoleAsync(
            organization.Id, "Invited Auditor", Permission.AuditView);
        var token = $"revoked-add-user-{Guid.NewGuid():N}";
        var tokenHash = Palladin.Module.Identity.Infrastructure.Jwt.TokenService.HashToken(token);
        await using (var seedScope = apiFactory.Services.CreateAsyncScope())
        {
            var writeContext = seedScope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
            writeContext.OrganizationInvitations.Add(OrganizationInvitation.Create(
                Guid.NewGuid(), organization.Id, organization.Name, invitationRole.Id, invitationRole.Name,
                inviter.Id, inviter.DisplayName, invitedUser.Email, "en", token, tokenHash,
                Duration.FromHours(72), apiFactory.FakeClock.GetCurrentInstant()));
            await writeContext.SaveChangesAsync();
        }

        var ownerClient = apiFactory.CreateAuthenticatedClient(owner, Permission.OrganizationManagement);
        var revokeResponse = await ownerClient.PUTAsync<
            UpdateOrganizationRoleEndpoint,
            UpdateOrganizationRoleRequest>(new UpdateOrganizationRoleRequest
            {
                RoleId = inviterRole.Id,
                Name = inviterRole.Name,
                Permissions = (int)Permission.AuditView,
            });
        var invitedClient = apiFactory.CreateAuthenticatedClient(invitedUser);
        var acceptResponse = await invitedClient.POSTAsync<
            AcceptOrganizationInvitationEndpoint,
            AcceptOrganizationInvitationRequest>(new AcceptOrganizationInvitationRequest { Token = token });

        revokeResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        acceptResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await acceptResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldContain("organization-invitation-role-unassignable");
        await using var verificationScope = apiFactory.Services.CreateAsyncScope();
        var readContext = verificationScope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        (await readContext.OrganizationInvitations.SingleAsync(invitation => invitation.TokenHash == tokenHash))
            .AcceptedAt.ShouldBeNull();
        (await readContext.OrganizationMembers.AnyAsync(member =>
            member.OrganizationId == organization.Id && member.UserId == invitedUser.Id)).ShouldBeFalse();
    }

    [Fact]
    public async Task When_DeletingRoleReferencedByHistoricalInvitations_Then_Returns409()
    {
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var role = await CreateRoleAsync(organization.Id, "Historical Role", Permission.AuditView);
        var now = apiFactory.FakeClock.GetCurrentInstant();
        await using (var seedScope = apiFactory.Services.CreateAsyncScope())
        {
            var writeContext = seedScope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
            var accepted = OrganizationInvitation.Create(
                Guid.NewGuid(), organization.Id, organization.Name, role.Id, role.Name,
                owner.Id, owner.DisplayName, "accepted-history@example.com", "en", "accepted-token",
                Guid.NewGuid().ToString("N"), Duration.FromHours(72), now);
            accepted.Accept(now);
            var expired = OrganizationInvitation.Create(
                Guid.NewGuid(), organization.Id, organization.Name, role.Id, role.Name,
                owner.Id, owner.DisplayName, "expired-history@example.com", "en", "expired-token",
                Guid.NewGuid().ToString("N"), Duration.FromHours(-1), now);
            writeContext.OrganizationInvitations.AddRange(accepted, expired);
            await writeContext.SaveChangesAsync();
        }

        var response = await apiFactory.CreateAuthenticatedClient(owner, Permission.OrganizationManagement)
            .DELETEAsync<DeleteOrganizationRoleEndpoint, DeleteOrganizationRoleRequest>(
                new DeleteOrganizationRoleRequest { RoleId = role.Id });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await using var verificationScope = apiFactory.Services.CreateAsyncScope();
        var readContext = verificationScope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        (await readContext.Roles.AnyAsync(candidate => candidate.Id == role.Id)).ShouldBeTrue();
        var invitations = await readContext.OrganizationInvitations
            .Where(invitation => invitation.OrganizationId == organization.Id
                                 && invitation.RoleName == role.Name)
            .ToListAsync();
        invitations.Count.ShouldBe(2);
        invitations.ShouldAllBe(invitation => invitation.RoleId == role.Id);
    }

    [Fact]
    public async Task When_DeletingRoleReferencedByActiveInvitation_Then_Returns409()
    {
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var role = await CreateRoleAsync(organization.Id, "Active Invitation Role", Permission.AuditView);
        await using (var seedScope = apiFactory.Services.CreateAsyncScope())
        {
            var writeContext = seedScope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
            writeContext.OrganizationInvitations.Add(OrganizationInvitation.Create(
                Guid.NewGuid(), organization.Id, organization.Name, role.Id, role.Name,
                owner.Id, owner.DisplayName, "active-invitation@example.com", "en", "active-token",
                Guid.NewGuid().ToString("N"), Duration.FromHours(72),
                apiFactory.FakeClock.GetCurrentInstant()));
            await writeContext.SaveChangesAsync();
        }

        var response = await apiFactory.CreateAuthenticatedClient(owner, Permission.OrganizationManagement)
            .DELETEAsync<DeleteOrganizationRoleEndpoint, DeleteOrganizationRoleRequest>(
                new DeleteOrganizationRoleRequest { RoleId = role.Id });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldContain("organization-role-in-use");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task When_RoleUpdateRacesMemberAssignment_Then_OrganizationFenceAllowsOnlyOneWriter(
        bool roleUpdateWins)
    {
        var (_, organization, _) = await apiFactory.Services.SeedUserAsync();
        var emptyRole = await CreateRoleAsync(organization.Id, "Empty", Permission.None);
        var candidateRole = await CreateRoleAsync(organization.Id, "Candidate", Permission.AuditView);
        var target = await SeedMemberWithRoleAsync(organization.Id, emptyRole);

        await using var assignmentScope = apiFactory.Services.CreateAsyncScope();
        await using var roleScope = apiFactory.Services.CreateAsyncScope();
        var assignmentContext = assignmentScope.ServiceProvider.GetRequiredService<IdentityDomainWriteContext>();
        var roleContext = roleScope.ServiceProvider.GetRequiredService<IdentityDomainWriteContext>();

        var assignmentOrganization = await assignmentContext.Organizations.SingleAsync(
            candidate => candidate.Id == organization.Id);
        assignmentOrganization.FenceMembershipMutation();
        var assignmentTarget = await assignmentContext.OrganizationMembers
            .Include(member => member.User)
            .Include(member => member.RoleAssignments)
                .ThenInclude(assignment => assignment.Role)
            .SingleAsync(member => member.OrganizationId == organization.Id && member.UserId == target.Id);
        var assignmentRole = await assignmentContext.Roles.SingleAsync(role => role.Id == candidateRole.Id);
        assignmentTarget.ReplaceRoles(
            [assignmentRole], target.DisplayName, Guid.NewGuid(), "concurrent actor",
            apiFactory.FakeClock.GetCurrentInstant());

        var roleOrganization = await roleContext.Organizations.SingleAsync(
            candidate => candidate.Id == organization.Id);
        roleOrganization.FenceMembershipMutation();
        var updatedRole = await roleContext.Roles.SingleAsync(role => role.Id == candidateRole.Id);
        updatedRole.UpdateCustom(updatedRole.Name, Permission.GrantManage, SystemClock.Instance.GetCurrentInstant());

        if (roleUpdateWins)
        {
            await roleContext.CommitAsync();
            await Should.ThrowAsync<DbUpdateConcurrencyException>(() => assignmentContext.CommitAsync());
        }
        else
        {
            await assignmentContext.CommitAsync();
            await Should.ThrowAsync<DbUpdateConcurrencyException>(() => roleContext.CommitAsync());
        }

        await using var verificationScope = apiFactory.Services.CreateAsyncScope();
        var readContext = verificationScope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        var persistedTarget = await readContext.OrganizationMembers
            .Include(member => member.RoleAssignments)
                .ThenInclude(assignment => assignment.Role)
            .SingleAsync(member => member.OrganizationId == organization.Id && member.UserId == target.Id);
        if (roleUpdateWins)
        {
            persistedTarget.EffectivePermissions().ShouldBe(Permission.None);
            (await readContext.Roles.SingleAsync(role => role.Id == candidateRole.Id))
                .Permissions.ShouldBe(Permission.GrantManage);
        }
        else
        {
            persistedTarget.EffectivePermissions().ShouldBe(Permission.AuditView);
            (await readContext.Roles.SingleAsync(role => role.Id == candidateRole.Id))
                .Permissions.ShouldBe(Permission.AuditView);
        }
    }

    [Fact]
    public async Task When_RoleUpdateRacesInvitationAcceptance_Then_FenceLeavesInvitationUnconsumed()
    {
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var (invitedUser, _, _) = await apiFactory.Services.SeedUserAsync();
        var role = await CreateRoleAsync(organization.Id, "Concurrent Invite", Permission.AuditView);
        var now = apiFactory.FakeClock.GetCurrentInstant();
        var tokenHash = Guid.NewGuid().ToString("N");
        await using (var seedScope = apiFactory.Services.CreateAsyncScope())
        {
            var writeContext = seedScope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
            writeContext.OrganizationInvitations.Add(OrganizationInvitation.Create(
                Guid.NewGuid(), organization.Id, organization.Name, role.Id, role.Name,
                owner.Id, owner.DisplayName, invitedUser.Email, "en", "concurrent-accept-token",
                tokenHash, Duration.FromHours(72), now));
            await writeContext.SaveChangesAsync();
        }

        await using var acceptanceScope = apiFactory.Services.CreateAsyncScope();
        await using var roleScope = apiFactory.Services.CreateAsyncScope();
        var acceptanceContext = acceptanceScope.ServiceProvider.GetRequiredService<IdentityDomainWriteContext>();
        var roleContext = roleScope.ServiceProvider.GetRequiredService<IdentityDomainWriteContext>();

        var acceptanceOrganization = await acceptanceContext.Organizations.SingleAsync(
            candidate => candidate.Id == organization.Id);
        acceptanceOrganization.FenceMembershipMutation();
        var invitation = await acceptanceContext.OrganizationInvitations
            .Include(candidate => candidate.Role)
            .SingleAsync(candidate => candidate.TokenHash == tokenHash);
        var acceptanceUser = await acceptanceContext.Users.SingleAsync(user => user.Id == invitedUser.Id);
        invitation.Accept(now);
        acceptanceContext.Add(OrganizationMember.Create(
            organization.Id, invitedUser.Id, invitation.Role!,
            acceptanceUser.DisplayName, acceptanceUser.Email, now));

        var roleOrganization = await roleContext.Organizations.SingleAsync(
            candidate => candidate.Id == organization.Id);
        roleOrganization.FenceMembershipMutation();
        var updatedRole = await roleContext.Roles.SingleAsync(candidate => candidate.Id == role.Id);
        updatedRole.UpdateCustom(role.Name, Permission.GrantManage, SystemClock.Instance.GetCurrentInstant());
        await roleContext.CommitAsync();

        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => acceptanceContext.CommitAsync());
        await using var verificationScope = apiFactory.Services.CreateAsyncScope();
        var readContext = verificationScope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        (await readContext.OrganizationInvitations.SingleAsync(candidate => candidate.TokenHash == tokenHash))
            .AcceptedAt.ShouldBeNull();
        (await readContext.OrganizationMembers.AnyAsync(member =>
            member.OrganizationId == organization.Id && member.UserId == invitedUser.Id)).ShouldBeFalse();
    }

    private async Task<Role> CreateRoleAsync(Guid organizationId, string name, Permission permissions)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
        var role = Role.Create(
            Guid.NewGuid(), organizationId, name, permissions,
            isSystem: false, apiFactory.FakeClock.GetCurrentInstant());
        writeContext.Roles.Add(role);
        await writeContext.SaveChangesAsync();
        return role;
    }

    private async Task<Role> GetRoleAsync(Guid organizationId, string name)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>()
            .Roles.SingleAsync(x => x.OrganizationId == organizationId && x.Name == name);
    }

    private async Task<User> SeedMemberWithRoleAsync(Guid organizationId, Role role)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
        var persistedRole = await writeContext.Roles.SingleAsync(x => x.Id == role.Id);
        var user = UserFaker.Create()
            .RuleFor(x => x.OrganizationId, organizationId)
            .RuleFor(x => x.EmailVerified, true)
            .Generate();
        writeContext.Users.Add(user);
        writeContext.OrganizationMembers.Add(OrganizationMember.Create(
            organizationId,
            user.Id,
            persistedRole,
            user.DisplayName,
            user.Email,
            apiFactory.FakeClock.GetCurrentInstant()));
        await writeContext.SaveChangesAsync();
        return user;
    }

    private async Task<User> SeedMemberWithRolesAsync(Guid organizationId, IReadOnlyCollection<Role> roles)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
        var roleIds = roles.Select(role => role.Id).ToArray();
        var persistedRoles = await writeContext.Roles
            .Where(role => roleIds.Contains(role.Id))
            .ToListAsync();
        var user = UserFaker.Create()
            .RuleFor(candidate => candidate.OrganizationId, organizationId)
            .RuleFor(candidate => candidate.EmailVerified, true)
            .Generate();
        writeContext.Users.Add(user);
        var membership = OrganizationMember.Create(
            organizationId,
            user.Id,
            persistedRoles[0],
            user.DisplayName,
            user.Email,
            apiFactory.FakeClock.GetCurrentInstant());
        foreach (var role in persistedRoles.Skip(1))
        {
            membership.RoleAssignments.Add(OrganizationMemberRole.Create(
                organizationId, user.Id, role));
        }

        writeContext.OrganizationMembers.Add(membership);
        await writeContext.SaveChangesAsync();
        return user;
    }
}
