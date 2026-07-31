using System.Net;
using System.Net.Http.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Palladin.Core.Security;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Triggers;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Mocks;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault;

[Collection<ApiFactoryCollection>]
public sealed class ListVaultMembersTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_VaultMembersArePaged_Then_EveryMemberAppearsExactlyOnce()
    {
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var second = await apiFactory.Services.SeedAdditionalOrganizationMemberAsync(organization.Id);
        var third = await apiFactory.Services.SeedAdditionalOrganizationMemberAsync(organization.Id);
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, owner.Id);
        await AddVaultMemberAsync(organization.Id, vault.Id, second.Id);
        await AddVaultMemberAsync(organization.Id, vault.Id, third.Id);
        var client = apiFactory.CreateAuthenticatedClient(owner, Permission.VaultManage);

        var memberIds = new List<Guid>();
        Guid? afterId = null;
        do
        {
            var response = await client.GetAsync(
                $"api/vaults/{vault.Id}/members?pageSize=1{(afterId is null ? string.Empty : $"&afterId={afterId}")}",
                TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var page = await response.Content.ReadFromJsonAsync<TestResponse>(TestContext.Current.CancellationToken);
            page.ShouldNotBeNull();
            page.Items.ShouldHaveSingleItem().DeprovisioningStatus.ShouldBe("Active");
            memberIds.Add(page.Items[0].MemberId);
            afterId = page.NextAfterId;
        } while (afterId is not null);

        memberIds.Count.ShouldBe(3);
        memberIds.Distinct().Count().ShouldBe(3);
        memberIds.ShouldBe(memberIds.Order().ToArray());
    }

    [Fact]
    public async Task When_UserReplicaExists_Then_MemberNameIsReturnedWithNullableFallback()
    {
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var memberWithoutReplica = await apiFactory.Services.SeedAdditionalOrganizationMemberAsync(organization.Id);
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, owner.Id);
        await AddVaultMemberAsync(organization.Id, vault.Id, memberWithoutReplica.Id);
        await SeedUserReplicaAsync(owner.Id, "Vault owner");
        var client = apiFactory.CreateAuthenticatedClient(owner, Permission.VaultManage);

        var response = await client.GetAsync($"api/vaults/{vault.Id}/members", TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<TestResponse>(TestContext.Current.CancellationToken);

        result!.Items.Single(x => x.MemberId == owner.Id).MemberName.ShouldBe("Vault owner");
        result.Items.Single(x => x.MemberId == memberWithoutReplica.Id).MemberName.ShouldBeNull();
    }

    [Fact]
    public async Task When_CallerIsNotAVaultMember_Then_DirectoryIsNotEnumerable()
    {
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var outsider = await apiFactory.Services.SeedAdditionalOrganizationMemberAsync(organization.Id);
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, owner.Id);
        var client = apiFactory.CreateAuthenticatedClient(outsider, Permission.VaultManage);

        var response = await client.GetAsync($"api/vaults/{vault.Id}/members", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task When_VaultBelongsToAnotherOrganization_Then_DirectoryIsNotEnumerable()
    {
        var (member, _, _) = await apiFactory.Services.SeedUserAsync();
        var (foreignOwner, foreignOrganization, _) = await apiFactory.Services.SeedUserAsync();
        var foreignVault = await apiFactory.Services.SeedVaultAsync(foreignOrganization.Id, foreignOwner.Id);
        var client = apiFactory.CreateAuthenticatedClient(member, Permission.VaultManage);

        var response = await client.GetAsync(
            $"api/vaults/{foreignVault.Id}/members",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task When_RemovalWaitsForThisVaultRotation_Then_MemberIsNotShownAsRemoved()
    {
        var (removedMember, organization, _) = await apiFactory.Services.SeedUserAsync();
        var remainingMember = await apiFactory.Services.SeedAdditionalOrganizationMemberAsync(organization.Id);
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, removedMember.Id);
        await AddVaultMemberAsync(organization.Id, vault.Id, remainingMember.Id);
        await RequestRemovalAsync(organization.Id, removedMember.Id, remainingMember.Id);
        var client = apiFactory.CreateAuthenticatedClient(remainingMember, Permission.VaultManage);

        var response = await client.GetAsync($"api/vaults/{vault.Id}/members", TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<TestResponse>(TestContext.Current.CancellationToken);
        var removed = result!.Items.Single(x => x.MemberId == removedMember.Id);

        removed.DeprovisioningStatus.ShouldBe("WaitingForRotation");
        removed.RotationId.ShouldNotBeNull();
    }

    [Fact]
    public async Task When_RemovalWouldLeaveNoMember_Then_LastMemberBlockerIsExplicit()
    {
        var (member, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, member.Id);
        await RequestRemovalAsync(organization.Id, member.Id, Guid.NewGuid());
        var client = apiFactory.CreateAuthenticatedClient(member, Permission.VaultManage);

        var response = await client.GetAsync($"api/vaults/{vault.Id}/members", TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<TestResponse>(TestContext.Current.CancellationToken);
        var blocked = result!.Items.ShouldHaveSingleItem();

        blocked.MemberId.ShouldBe(member.Id);
        blocked.DeprovisioningStatus.ShouldBe("BlockedLastMember");
        blocked.RotationId.ShouldBeNull();
    }

    [Fact]
    public async Task When_PageSizeIsUnboundedOrCursorMalformed_Then_RequestIsRejected()
    {
        var (member, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, member.Id);
        var client = apiFactory.CreateAuthenticatedClient(member, Permission.VaultManage);

        (await client.GetAsync(
                $"api/vaults/{vault.Id}/members?pageSize=101",
                TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.GetAsync(
                $"api/vaults/{vault.Id}/members?afterId=not-a-guid",
                TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private async Task AddVaultMemberAsync(Guid organizationId, Guid vaultId, Guid userId)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
        var vault = await writeContext.Vaults
            .Include(x => x.VaultMembers)
            .Include(x => x.VaultMemberKeyEnvelopes)
            .SingleAsync(x => x.OrganizationId == organizationId && x.Id == vaultId);
        vault.AddMember(userId, VaultFaker.CreateMemberKey(vault.Scope, userId), apiFactory.FakeClock.GetCurrentInstant());
        await writeContext.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task RequestRemovalAsync(Guid organizationId, Guid memberId, Guid requestedBy)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var consumer = ActivatorUtilities.CreateInstance<OnOrganizationMemberRemovalRequested>(scope.ServiceProvider);
        var message = new OrganizationMemberRemovalRequestedEvent(
            Guid.NewGuid(),
            organizationId,
            memberId,
            requestedBy,
            apiFactory.FakeClock.GetCurrentInstant());
        await consumer.Consume(apiFactory.MockConsumeContext(message));
    }

    private async Task SeedUserReplicaAsync(Guid userId, string displayName)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
        var now = apiFactory.FakeClock.GetCurrentInstant();
        writeContext.Users.Add(Palladin.Module.Vault.Domain.User.Create(userId, displayName, now, now));
        await writeContext.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private sealed record TestItem(
        Guid MemberId,
        string? MemberName,
        string AddedAt,
        string DeprovisioningStatus,
        Guid? RotationId);

    private sealed record TestResponse(IReadOnlyList<TestItem> Items, Guid? NextAfterId);
}
