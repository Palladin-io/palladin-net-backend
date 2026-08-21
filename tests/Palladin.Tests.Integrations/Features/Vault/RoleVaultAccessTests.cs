using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Triggers;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Mocks;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault;

[Collection<ApiFactoryCollection>]
public sealed class RoleVaultAccessTests(ApiFactory apiFactory) : TestBase
{
    private static readonly Permission RequiredPermissions =
        Permission.OrganizationManagement | Permission.VaultManage;

    [Fact]
    public async Task When_DesiredAccessIsChangedAndRetried_Then_OnePendingOperationExistsWithoutVaultMembershipMutation()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var roleId = Guid.NewGuid();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var awaitingMemberId = Guid.NewGuid();
        await SeedDirectoryAsync(organization.Id, roleId, user.Id, awaitingMemberId);
        apiFactory.MockId(Guid.NewGuid());
        var client = apiFactory.CreateAuthenticatedClient(user, RequiredPermissions);
        var request = new UpdateRoleVaultAccessRequest
        {
            RoleId = roleId,
            VaultIds = [vault.Id],
        };

        await using var beforeScope = apiFactory.Services.CreateAsyncScope();
        var beforeContext = beforeScope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var memberCountBefore = await beforeContext.VaultMembers.CountAsync(x => x.VaultId == vault.Id);
        var envelopeCountBefore = await beforeContext.VaultMemberKeyEnvelopes.CountAsync(x => x.VaultId == vault.Id);

        // When
        var firstResponse = await client.PutAsJsonAsync(
            $"api/organization/roles/{roleId}/vault-access", request);
        var first = await firstResponse.Content.ReadFromJsonAsync<UpdateRoleVaultAccessResponse>();
        var retryResponse = await client.PutAsJsonAsync(
            $"api/organization/roles/{roleId}/vault-access", request);
        var retry = await retryResponse.Content.ReadFromJsonAsync<UpdateRoleVaultAccessResponse>();

        // Then
        firstResponse.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        retryResponse.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        first.ShouldNotBeNull();
        retry.ShouldNotBeNull();
        first.OperationId.ShouldNotBeNull();
        retry.OperationId.ShouldBe(first.OperationId);
        first.Status.ShouldBe("awaiting-provisioner");
        first.Impact.AddsAwaitingProvisioning.ShouldBe(1);
        first.Impact.Unchanged.ShouldBe(1);

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await readContext.RoleVaultAccessOperations.CountAsync(x => x.RoleId == roleId)).ShouldBe(1);
        (await readContext.RoleVaultAccessPolicies.SingleAsync(x => x.RoleId == roleId)).VaultId.ShouldBe(vault.Id);
        (await readContext.VaultMembers.CountAsync(x => x.VaultId == vault.Id)).ShouldBe(memberCountBefore);
        (await readContext.VaultMemberKeyEnvelopes.CountAsync(x => x.VaultId == vault.Id)).ShouldBe(envelopeCountBefore);
        (await readContext.VaultMembers.AnyAsync(x =>
            x.VaultId == vault.Id && x.UserId == awaitingMemberId)).ShouldBeFalse();

        var policy = await client.GetFromJsonAsync<GetRoleVaultAccessResponse>(
            $"api/organization/roles/{roleId}/vault-access");
        policy.ShouldNotBeNull();
        policy.SelectedVaultIds.ShouldBe([vault.Id]);
        policy.PendingOperation.ShouldNotBeNull();
        policy.PendingOperation.OperationId.ShouldBe(first.OperationId.Value);

        var operationResponse = await client.GetAsync(
            $"api/organization/role-vault-access-operations/{first.OperationId.Value}");
        operationResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var operation = JsonDocument.Parse(await operationResponse.Content.ReadAsStringAsync());
        operation.RootElement.GetProperty("status").GetString().ShouldBe("awaiting-provisioner");
    }

    [Fact]
    public async Task When_DefaultVaultIsSelected_Then_RejectsWithoutPolicyOrOperation()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var roleId = Guid.NewGuid();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id, isDefault: true);
        await SeedDirectoryAsync(organization.Id, roleId, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user, RequiredPermissions);

        // When
        var response = await client.PutAsJsonAsync(
            $"api/organization/roles/{roleId}/vault-access",
            new UpdateRoleVaultAccessRequest { RoleId = roleId, VaultIds = [vault.Id] });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await readContext.RoleVaultAccessPolicySets.AnyAsync(x => x.RoleId == roleId)).ShouldBeFalse();
        (await readContext.RoleVaultAccessOperations.AnyAsync(x => x.RoleId == roleId)).ShouldBeFalse();
    }

    [Fact]
    public async Task When_ForeignTenantRoleOrVaultIsSubmitted_Then_FailsClosed()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var (foreignUser, foreignOrganization, _) = await apiFactory.Services.SeedUserAsync();
        var roleId = Guid.NewGuid();
        var foreignRoleId = Guid.NewGuid();
        var foreignVault = await apiFactory.Services.SeedVaultAsync(foreignOrganization.Id, foreignUser.Id);
        await SeedDirectoryAsync(organization.Id, roleId, user.Id);
        await SeedDirectoryAsync(foreignOrganization.Id, foreignRoleId, foreignUser.Id);
        var client = apiFactory.CreateAuthenticatedClient(user, RequiredPermissions);

        // When
        var foreignRoleResponse = await client.PutAsJsonAsync(
            $"api/organization/roles/{foreignRoleId}/vault-access",
            new UpdateRoleVaultAccessRequest { RoleId = foreignRoleId, VaultIds = [] });
        var foreignVaultResponse = await client.PutAsJsonAsync(
            $"api/organization/roles/{roleId}/vault-access",
            new UpdateRoleVaultAccessRequest { RoleId = roleId, VaultIds = [foreignVault.Id] });

        // Then
        foreignRoleResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        foreignVaultResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await readContext.RoleVaultAccessPolicySets.AnyAsync(x =>
            x.OrganizationId == organization.Id)).ShouldBeFalse();
    }

    [Fact]
    public async Task When_CompetingPolicyReplacementsRace_Then_FinalPolicyIsOneCompleteRequest()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var roleId = Guid.NewGuid();
        var firstVault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var secondVault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        await SeedDirectoryAsync(organization.Id, roleId, user.Id);
        apiFactory.MockId(Guid.NewGuid());
        var firstClient = apiFactory.CreateAuthenticatedClient(user, RequiredPermissions);
        var secondClient = apiFactory.CreateAuthenticatedClient(user, RequiredPermissions);

        // When
        var responses = await Task.WhenAll(
            firstClient.PutAsJsonAsync(
                $"api/organization/roles/{roleId}/vault-access",
                new UpdateRoleVaultAccessRequest { RoleId = roleId, VaultIds = [firstVault.Id] }),
            secondClient.PutAsJsonAsync(
                $"api/organization/roles/{roleId}/vault-access",
                new UpdateRoleVaultAccessRequest { RoleId = roleId, VaultIds = [secondVault.Id] }));

        // Then
        responses.ShouldContain(response => response.StatusCode == HttpStatusCode.Accepted);
        responses.All(response =>
                response.StatusCode == HttpStatusCode.Accepted
                || response.StatusCode == HttpStatusCode.Conflict)
            .ShouldBeTrue();
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var selectedVaultIds = await readContext.RoleVaultAccessPolicies
            .Where(x => x.OrganizationId == organization.Id && x.RoleId == roleId)
            .Select(x => x.VaultId)
            .ToArrayAsync();
        selectedVaultIds.Length.ShouldBe(1);
        new[] { firstVault.Id, secondVault.Id }.ShouldContain(selectedVaultIds[0]);
        (await readContext.VaultMembers.CountAsync(x =>
            x.OrganizationId == organization.Id
            && (x.VaultId == firstVault.Id || x.VaultId == secondVault.Id))).ShouldBe(2);
    }

    [Fact]
    public async Task When_MemberAssignmentWinsFirstPolicyCreateRace_Then_StalePolicyCannotCommitAndRetryIncludesMember()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var roleId = Guid.NewGuid();
        var newlyAssignedMemberId = Guid.NewGuid();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        await SeedDirectoryAsync(organization.Id, roleId, user.Id);
        await using (var seedScope = apiFactory.Services.CreateAsyncScope())
        {
            var seedContext = seedScope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
            seedContext.OrganizationMemberRoleSets.Add(OrganizationMemberRoleSet.Create(
                organization.Id,
                newlyAssignedMemberId,
                [],
                1,
                1,
                isActive: true,
                apiFactory.FakeClock.GetCurrentInstant()));
            await seedContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var staleScope = apiFactory.Services.CreateAsyncScope();
        var staleContext = staleScope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>();
        var staleRole = await staleContext.OrganizationRoleDirectory.SingleAsync(
            x => x.OrganizationId == organization.Id && x.RoleId == roleId,
            TestContext.Current.CancellationToken);
        staleRole.FencePolicyMutation();
        var staleOperation = RoleVaultAccessOperation.Create(
            organization.Id,
            Guid.NewGuid(),
            roleId,
            0,
            0,
            1,
            user.Id,
            apiFactory.FakeClock.GetCurrentInstant());
        staleContext.Add(staleOperation);
        staleContext.Add(RoleVaultAccessPolicySet.Create(
            organization.Id,
            roleId,
            [vault.Id],
            staleOperation.Id,
            user.Id,
            1,
            RequiredPermissions,
            apiFactory.FakeClock.GetCurrentInstant()));

        // When: the assignment consumer reads no committed policy and wins the shared role fence.
        await apiFactory.ConsumeAsync<OnOrganizationMemberRolesUpserted, OrganizationMemberRolesUpsertedEvent>(
            new OrganizationMemberRolesUpsertedEvent(
                organization.Id,
                newlyAssignedMemberId,
                [roleId],
                2,
                1,
                IsActive: true,
                apiFactory.FakeClock.GetCurrentInstant()),
            TestContext.Current.CancellationToken);
        await Should.ThrowAsync<DbUpdateConcurrencyException>(
            () => staleContext.CommitAsync(TestContext.Current.CancellationToken));

        apiFactory.MockId(Guid.NewGuid());
        var client = apiFactory.CreateAuthenticatedClient(user, RequiredPermissions);
        var retryResponse = await client.PutAsJsonAsync(
            $"api/organization/roles/{roleId}/vault-access",
            new UpdateRoleVaultAccessRequest { RoleId = roleId, VaultIds = [vault.Id] },
            TestContext.Current.CancellationToken);
        var retry = await retryResponse.Content.ReadFromJsonAsync<UpdateRoleVaultAccessResponse>(
            TestContext.Current.CancellationToken);

        // Then
        retryResponse.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        retry.ShouldNotBeNull();
        retry.Impact.AddsAwaitingProvisioning.ShouldBe(1);
        retry.Impact.Unchanged.ShouldBe(1);
        await using var assertScope = apiFactory.Services.CreateAsyncScope();
        var readContext = assertScope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await readContext.RoleVaultAccessOperations.CountAsync(
            x => x.OrganizationId == organization.Id && x.RoleId == roleId,
            TestContext.Current.CancellationToken)).ShouldBe(1);
        (await readContext.VaultMembers.AnyAsync(
            x => x.OrganizationId == organization.Id
                 && x.VaultId == vault.Id
                 && x.UserId == newlyAssignedMemberId,
            TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task When_LastDesiredVaultIsRemoved_Then_RemovalStaysPendingAndMembershipRemains()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var roleId = Guid.NewGuid();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        await SeedDirectoryAsync(organization.Id, roleId, user.Id);
        apiFactory.MockId(Guid.NewGuid());
        var client = apiFactory.CreateAuthenticatedClient(user, RequiredPermissions);
        await client.PutAsJsonAsync(
            $"api/organization/roles/{roleId}/vault-access",
            new UpdateRoleVaultAccessRequest { RoleId = roleId, VaultIds = [vault.Id] });

        // When
        var response = await client.PutAsJsonAsync(
            $"api/organization/roles/{roleId}/vault-access",
            new UpdateRoleVaultAccessRequest { RoleId = roleId, VaultIds = [] });
        var result = await response.Content.ReadFromJsonAsync<UpdateRoleVaultAccessResponse>();

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        result.ShouldNotBeNull();
        result.Status.ShouldBe("awaiting-provisioner");
        result.Impact.RemovalsAwaitingSourceReconciliation.ShouldBe(1);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await readContext.RoleVaultAccessPolicies.AnyAsync(x => x.RoleId == roleId)).ShouldBeFalse();
        (await readContext.VaultMembers.AnyAsync(x =>
            x.OrganizationId == organization.Id && x.VaultId == vault.Id && x.UserId == user.Id)).ShouldBeTrue();
        (await readContext.VaultMemberKeyEnvelopes.AnyAsync(x =>
            x.OrganizationId == organization.Id && x.VaultId == vault.Id && x.MemberId == user.Id)).ShouldBeTrue();
    }

    [Fact]
    public async Task When_OwnerSubmitsAdministratorPolicy_Then_AcceptsWithoutCryptographicMembershipMutation()
    {
        // Given
        var (owner, organization, administratorRole) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, owner.Id);
        await SeedRoleDirectoryEntriesAsync(
            organization.Id,
            [(administratorRole.Id, true, administratorRole.Permissions)]);
        apiFactory.MockId(Guid.NewGuid());
        var client = apiFactory.CreateAuthenticatedClient(owner, (Permission)int.MaxValue);
        var (memberCountBefore, envelopeCountBefore) = await GetVaultCryptoCountsAsync(
            organization.Id,
            vault.Id);

        // When
        var response = await client.PutAsJsonAsync(
            $"api/organization/roles/{administratorRole.Id}/vault-access",
            new UpdateRoleVaultAccessRequest
            {
                RoleId = administratorRole.Id,
                VaultIds = [vault.Id],
            },
            TestContext.Current.CancellationToken);
        var getResponse = await client.GetAsync(
            $"api/organization/roles/{administratorRole.Id}/vault-access",
            TestContext.Current.CancellationToken);
        var policyResponse = await getResponse.Content.ReadFromJsonAsync<GetRoleVaultAccessResponse>(
            TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        getResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        policyResponse.ShouldNotBeNull();
        policyResponse.SelectedVaultIds.ShouldBe([vault.Id]);
        await using var assertScope = apiFactory.Services.CreateAsyncScope();
        var readContext = assertScope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await readContext.RoleVaultAccessPolicies.SingleAsync(
            x => x.OrganizationId == organization.Id && x.RoleId == administratorRole.Id,
            TestContext.Current.CancellationToken)).VaultId.ShouldBe(vault.Id);
        (await readContext.RoleVaultAccessOperations.CountAsync(
            x => x.OrganizationId == organization.Id && x.RoleId == administratorRole.Id,
            TestContext.Current.CancellationToken)).ShouldBe(1);
        (await readContext.VaultMembers.CountAsync(
            x => x.OrganizationId == organization.Id && x.VaultId == vault.Id,
            TestContext.Current.CancellationToken)).ShouldBe(memberCountBefore);
        (await readContext.VaultMemberKeyEnvelopes.CountAsync(
            x => x.OrganizationId == organization.Id && x.VaultId == vault.Id,
            TestContext.Current.CancellationToken)).ShouldBe(envelopeCountBefore);
    }

    [Fact]
    public async Task When_AuthorizedNonOwnerSubmitsUserRolePolicy_Then_AcceptsAndReplicaReplayPreservesPolicy()
    {
        // Given
        var (_, organization, _) = await apiFactory.Services.SeedUserAsync();
        var actorPermissions = RequiredPermissions | Permission.VaultCreate;
        var actor = await SeedMemberWithPermissionsAsync(organization.Id, actorPermissions);
        var userRole = await GetRoleAsync(organization.Id, Role.DefaultUserName);
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, actor.Id);
        await SeedRoleDirectoryEntriesAsync(
            organization.Id,
            [(userRole.Id, true, userRole.Permissions)]);
        apiFactory.MockId(Guid.NewGuid());
        var client = apiFactory.CreateAuthenticatedClient(actor, actorPermissions);
        var (memberCountBefore, envelopeCountBefore) = await GetVaultCryptoCountsAsync(
            organization.Id,
            vault.Id);

        // When
        var response = await client.PutAsJsonAsync(
            $"api/organization/roles/{userRole.Id}/vault-access",
            new UpdateRoleVaultAccessRequest { RoleId = userRole.Id, VaultIds = [vault.Id] },
            TestContext.Current.CancellationToken);
        await apiFactory.ConsumeAsync<OnOrganizationRoleUpserted, OrganizationRoleUpsertedEvent>(
            new OrganizationRoleUpsertedEvent(
                organization.Id,
                userRole.Id,
                2,
                EntityChange.Updated,
                IsSystem: true,
                userRole.Permissions,
                apiFactory.FakeClock.GetCurrentInstant()),
            TestContext.Current.CancellationToken);
        var getResponse = await client.GetAsync(
            $"api/organization/roles/{userRole.Id}/vault-access",
            TestContext.Current.CancellationToken);
        var policyResponse = await getResponse.Content.ReadFromJsonAsync<GetRoleVaultAccessResponse>(
            TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        getResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        policyResponse.ShouldNotBeNull();
        policyResponse.SelectedVaultIds.ShouldBe([vault.Id]);
        await using var assertScope = apiFactory.Services.CreateAsyncScope();
        var readContext = assertScope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var policy = await readContext.RoleVaultAccessPolicySets
            .Include(x => x.Policies)
            .SingleAsync(
                x => x.OrganizationId == organization.Id && x.RoleId == userRole.Id,
                TestContext.Current.CancellationToken);
        policy.AuthorizedRoleRevision.ShouldBe(2UL);
        policy.Policies.ShouldHaveSingleItem().VaultId.ShouldBe(vault.Id);
        (await readContext.RoleVaultAccessOperations.CountAsync(
            x => x.OrganizationId == organization.Id && x.RoleId == userRole.Id,
            TestContext.Current.CancellationToken)).ShouldBe(1);
        (await readContext.VaultMembers.CountAsync(
            x => x.OrganizationId == organization.Id && x.VaultId == vault.Id,
            TestContext.Current.CancellationToken)).ShouldBe(memberCountBefore);
        (await readContext.VaultMemberKeyEnvelopes.CountAsync(
            x => x.OrganizationId == organization.Id && x.VaultId == vault.Id,
            TestContext.Current.CancellationToken)).ShouldBe(envelopeCountBefore);
    }

    [Fact]
    public async Task When_NonOwnerSubmitsAdministratorPolicy_Then_RejectsWithoutMutation()
    {
        // Given
        var (_, organization, administratorRole) = await apiFactory.Services.SeedUserAsync();
        var actor = await SeedMemberWithPermissionsAsync(
            organization.Id,
            RequiredPermissions | Permission.VaultCreate);
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, actor.Id);
        await SeedRoleDirectoryEntriesAsync(
            organization.Id,
            [(administratorRole.Id, true, administratorRole.Permissions)]);
        var client = apiFactory.CreateAuthenticatedClient(actor, (Permission)int.MaxValue);

        // When
        var response = await client.PutAsJsonAsync(
            $"api/organization/roles/{administratorRole.Id}/vault-access",
            new UpdateRoleVaultAccessRequest
            {
                RoleId = administratorRole.Id,
                VaultIds = [vault.Id],
            },
            TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await AssertNoRolePolicyMutationAsync(organization.Id, administratorRole.Id);
    }

    [Fact]
    public async Task When_NonOwnerSubmitsUserRolePolicyAboveDelegationCeiling_Then_RejectsWithoutMutation()
    {
        // Given
        var (_, organization, _) = await apiFactory.Services.SeedUserAsync();
        var actor = await SeedMemberWithPermissionsAsync(organization.Id, RequiredPermissions);
        var userRole = await GetRoleAsync(organization.Id, Role.DefaultUserName);
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, actor.Id);
        await SeedRoleDirectoryEntriesAsync(
            organization.Id,
            [(userRole.Id, true, userRole.Permissions)]);
        var client = apiFactory.CreateAuthenticatedClient(actor, RequiredPermissions);

        // When
        var response = await client.PutAsJsonAsync(
            $"api/organization/roles/{userRole.Id}/vault-access",
            new UpdateRoleVaultAccessRequest { RoleId = userRole.Id, VaultIds = [vault.Id] },
            TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await AssertNoRolePolicyMutationAsync(organization.Id, userRole.Id);
    }

    [Fact]
    public async Task When_NonOwnerSubmitsPolicyForCustomRoleAboveDelegationCeiling_Then_RejectsWithoutMutation()
    {
        // Given
        var (_, organization, _) = await apiFactory.Services.SeedUserAsync();
        var member = await SeedMemberWithPermissionsAsync(organization.Id, RequiredPermissions);
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, member.Id);
        var higherRoleId = Guid.NewGuid();
        var higherPermissions = RequiredPermissions | Permission.AgentManage;
        await SeedDirectoryAsync(
            organization.Id,
            higherRoleId,
            member.Id,
            permissions: higherPermissions);
        var client = apiFactory.CreateAuthenticatedClient(member, RequiredPermissions);

        // When
        var response = await client.PutAsJsonAsync(
            $"api/organization/roles/{higherRoleId}/vault-access",
            new UpdateRoleVaultAccessRequest { RoleId = higherRoleId, VaultIds = [vault.Id] },
            TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await using var assertScope = apiFactory.Services.CreateAsyncScope();
        var readContext = assertScope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await readContext.RoleVaultAccessPolicySets.AnyAsync(
            x => x.RoleId == higherRoleId,
            TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await readContext.RoleVaultAccessOperations.AnyAsync(
            x => x.RoleId == higherRoleId,
            TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task When_RoleIsElevatedAfterPolicyCommit_Then_ConsumerInvalidatesDesiredAccessWithoutCryptoMutation()
    {
        // Given
        var (_, organization, _) = await apiFactory.Services.SeedUserAsync();
        var member = await SeedMemberWithPermissionsAsync(organization.Id, RequiredPermissions);
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, member.Id);
        var roleId = Guid.NewGuid();
        await SeedDirectoryAsync(organization.Id, roleId, member.Id);
        var client = apiFactory.CreateAuthenticatedClient(member, RequiredPermissions);
        apiFactory.MockId(Guid.NewGuid());
        var createResponse = await client.PutAsJsonAsync(
            $"api/organization/roles/{roleId}/vault-access",
            new UpdateRoleVaultAccessRequest { RoleId = roleId, VaultIds = [vault.Id] },
            TestContext.Current.CancellationToken);
        createResponse.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var created = await createResponse.Content.ReadFromJsonAsync<UpdateRoleVaultAccessResponse>(
            TestContext.Current.CancellationToken);
        created.ShouldNotBeNull();

        int memberCountBefore;
        int envelopeCountBefore;
        await using (var beforeScope = apiFactory.Services.CreateAsyncScope())
        {
            var beforeContext = beforeScope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
            memberCountBefore = await beforeContext.VaultMembers.CountAsync(
                x => x.OrganizationId == organization.Id && x.VaultId == vault.Id,
                TestContext.Current.CancellationToken);
            envelopeCountBefore = await beforeContext.VaultMemberKeyEnvelopes.CountAsync(
                x => x.OrganizationId == organization.Id && x.VaultId == vault.Id,
                TestContext.Current.CancellationToken);
        }

        var elevatedPermissions = RequiredPermissions | Permission.AgentManage;
        await using (var identityScope = apiFactory.Services.CreateAsyncScope())
        {
            var identityContext = identityScope.ServiceProvider.GetRequiredService<IdentityDomainWriteContext>();
            var role = await identityContext.Roles.SingleAsync(
                x => x.OrganizationId == organization.Id && x.Id == roleId,
                TestContext.Current.CancellationToken);
            role.UpdateCustom("Elevated Vault role", elevatedPermissions, apiFactory.FakeClock.GetCurrentInstant())
                .ShouldBeTrue();
            await identityContext.CommitAsync(TestContext.Current.CancellationToken);
        }

        apiFactory.MockId(Guid.NewGuid());

        // When
        await apiFactory.ConsumeAsync<OnOrganizationRoleUpserted, OrganizationRoleUpsertedEvent>(
            new OrganizationRoleUpsertedEvent(
                organization.Id,
                roleId,
                2,
                EntityChange.Updated,
                IsSystem: false,
                elevatedPermissions,
                apiFactory.FakeClock.GetCurrentInstant()),
            TestContext.Current.CancellationToken);

        // Then
        await using var assertScope = apiFactory.Services.CreateAsyncScope();
        var readContext = assertScope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await readContext.RoleVaultAccessPolicies.AnyAsync(
            x => x.OrganizationId == organization.Id && x.RoleId == roleId,
            TestContext.Current.CancellationToken)).ShouldBeFalse();
        var operations = await readContext.RoleVaultAccessOperations
            .Where(x => x.OrganizationId == organization.Id && x.RoleId == roleId)
            .OrderBy(x => x.CreatedAt)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        operations.Length.ShouldBe(2);
        operations.Single(x => x.Id == created.OperationId).Status
            .ShouldBe(RoleVaultAccessOperationStatus.Superseded);
        var reconciliation = operations.Single(x => x.Id != created.OperationId);
        reconciliation.Status.ShouldBe(RoleVaultAccessOperationStatus.AwaitingProvisioner);
        reconciliation.AddsAwaitingProvisioning.ShouldBe(0);
        reconciliation.RemovalsAwaitingSourceReconciliation.ShouldBe(1);
        (await readContext.VaultMembers.CountAsync(
            x => x.OrganizationId == organization.Id && x.VaultId == vault.Id,
            TestContext.Current.CancellationToken)).ShouldBe(memberCountBefore);
        (await readContext.VaultMemberKeyEnvelopes.CountAsync(
            x => x.OrganizationId == organization.Id && x.VaultId == vault.Id,
            TestContext.Current.CancellationToken)).ShouldBe(envelopeCountBefore);
    }

    private async Task<Palladin.Module.Identity.Domain.User> SeedMemberWithPermissionsAsync(
        Guid organizationId,
        Permission permissions)
    {
        var user = await apiFactory.Services.SeedAdditionalOrganizationMemberAsync(organizationId);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
        var role = Role.Create(
            Guid.NewGuid(),
            organizationId,
            $"Vault policy actor {Guid.NewGuid():N}",
            permissions,
            isSystem: false,
            apiFactory.FakeClock.GetCurrentInstant());
        writeContext.Roles.Add(role);
        var membership = await writeContext.OrganizationMembers
            .Include(x => x.RoleAssignments)
                .ThenInclude(x => x.Role)
            .SingleAsync(
                x => x.OrganizationId == organizationId && x.UserId == user.Id,
                TestContext.Current.CancellationToken);
        membership.ReplaceRoles(
                [role],
                user.DisplayName,
                user.Id,
                user.DisplayName,
                apiFactory.FakeClock.GetCurrentInstant())
            .ShouldBeTrue();
        await writeContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        return user;
    }

    private async Task<Role> GetRoleAsync(Guid organizationId, string roleName)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>()
            .Roles.SingleAsync(
                x => x.OrganizationId == organizationId && x.Name == roleName,
                TestContext.Current.CancellationToken);
    }

    private async Task<(int MemberCount, int EnvelopeCount)> GetVaultCryptoCountsAsync(
        Guid organizationId,
        Guid vaultId)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        return (
            await readContext.VaultMembers.CountAsync(
                x => x.OrganizationId == organizationId && x.VaultId == vaultId,
                TestContext.Current.CancellationToken),
            await readContext.VaultMemberKeyEnvelopes.CountAsync(
                x => x.OrganizationId == organizationId && x.VaultId == vaultId,
                TestContext.Current.CancellationToken));
    }

    private async Task AssertNoRolePolicyMutationAsync(Guid organizationId, Guid roleId)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await readContext.RoleVaultAccessPolicySets.AnyAsync(
            x => x.OrganizationId == organizationId && x.RoleId == roleId,
            TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await readContext.RoleVaultAccessOperations.AnyAsync(
            x => x.OrganizationId == organizationId && x.RoleId == roleId,
            TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    private async Task SeedDirectoryAsync(
        Guid organizationId,
        Guid roleId,
        Guid activeMemberId,
        Guid? secondActiveMemberId = null,
        Permission? permissions = null)
    {
        await using (var identityScope = apiFactory.Services.CreateAsyncScope())
        {
            var identityContext = identityScope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
            if (!await identityContext.Roles.AnyAsync(
                    x => x.OrganizationId == organizationId && x.Id == roleId,
                    TestContext.Current.CancellationToken))
            {
                identityContext.Roles.Add(Role.Create(
                    roleId,
                    organizationId,
                    $"Vault role {roleId:N}",
                    permissions ?? RequiredPermissions,
                    isSystem: false,
                    apiFactory.FakeClock.GetCurrentInstant()));
                await identityContext.SaveChangesAsync(TestContext.Current.CancellationToken);
            }
        }

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
        writeContext.OrganizationRoleDirectory.Add(OrganizationRoleDirectoryEntry.Create(
            organizationId,
            roleId,
            1,
            isSystem: false,
            permissions ?? RequiredPermissions,
            apiFactory.FakeClock.GetCurrentInstant()));
        writeContext.OrganizationMemberRoleSets.Add(OrganizationMemberRoleSet.Create(
            organizationId,
            activeMemberId,
            [roleId],
            1,
            1,
            isActive: true,
            updatedAt: apiFactory.FakeClock.GetCurrentInstant()));
        if (secondActiveMemberId is { } secondMemberId)
        {
            writeContext.OrganizationMemberRoleSets.Add(OrganizationMemberRoleSet.Create(
                organizationId,
                secondMemberId,
                [roleId],
                1,
                1,
                isActive: true,
                updatedAt: apiFactory.FakeClock.GetCurrentInstant()));
        }

        await writeContext.SaveChangesAsync();
    }

    private async Task SeedRoleDirectoryEntriesAsync(
        Guid organizationId,
        IReadOnlyCollection<(Guid RoleId, bool IsSystem, Permission Permissions)> roles)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
        foreach (var role in roles)
        {
            writeContext.OrganizationRoleDirectory.Add(OrganizationRoleDirectoryEntry.Create(
                organizationId,
                role.RoleId,
                1,
                role.IsSystem,
                role.Permissions,
                apiFactory.FakeClock.GetCurrentInstant()));
        }

        await writeContext.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
