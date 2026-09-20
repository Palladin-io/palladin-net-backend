using System.Net;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FastEndpoints;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NodaTime;
using NSubstitute;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Features;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Infrastructure.Sharing;
using Palladin.Module.Vault.Contracts.Commands;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Sharing;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Identity;

[Collection<ApiFactoryCollection>]
public sealed class EntrySharingRevocationTests(ApiFactory apiFactory) : TestBase
{
    [Theory]
    [InlineData("removal", false)]
    [InlineData("assignment", false)]
    [InlineData("definition", false)]
    [InlineData("removal", true)]
    [InlineData("assignment", true)]
    [InlineData("definition", true)]
    public async Task When_RevocationAcknowledgementIsLost_Then_IdentityDoesNotCommitAndAnExactRetryCanFinish(
        string change, bool commandWasCommitted)
    {
        // Given
        var seeded = await SeedShareAsync();
        var client = Substitute.For<IRequestClient<RevokeMemberEntrySharingCommand>>();
        client.GetResponse<MemberEntrySharingRevoked>(Arg.Any<RevokeMemberEntrySharingCommand>(),
                Arg.Any<CancellationToken>(), Arg.Any<RequestTimeout>())
            .Returns(call => LoseAcknowledgementAsync(call.Arg<RevokeMemberEntrySharingCommand>()));
        var revocation = new EntrySharingRevocation(client, Options.Create(new EntrySharingRevocationOptions()));
        await using var mutationScope = apiFactory.Services.CreateAsyncScope();
        var context = mutationScope.ServiceProvider.GetRequiredService<IdentityDomainWriteContext>();
        var claims = new JwtSecurityTokenHandler().ReadJwtToken(seeded.OwnerClient.DefaultRequestHeaders.Authorization!.Parameter).Claims;
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")),
            RequestServices = mutationScope.ServiceProvider,
        };

        // When
        Func<Task> mutate = change switch
        {
            "removal" => () => Factory.Create<RemoveOrganizationMemberEndpoint>(httpContext,
                context, apiFactory.GuidProvider, revocation, apiFactory.FakeClock).HandleAsync(
                new RemoveOrganizationMemberRequest { UserId = seeded.MemberId }, TestContext.Current.CancellationToken),
            "assignment" => () => Factory.Create<UpdateOrganizationMemberRolesEndpoint>(httpContext,
                context, revocation, apiFactory.FakeClock).HandleAsync(
                new UpdateOrganizationMemberRolesRequest { UserId = seeded.MemberId, RoleIds = [seeded.WithoutAccessRoleId] },
                TestContext.Current.CancellationToken),
            _ => () => Factory.Create<UpdateOrganizationRoleEndpoint>(httpContext,
                context, revocation, apiFactory.FakeClock).HandleAsync(new UpdateOrganizationRoleRequest
                {
                    RoleId = seeded.RoleId, Name = "Sharing sender", Permissions = (int)Permission.AuditView,
                }, TestContext.Current.CancellationToken),
        };
        var error = await Record.ExceptionAsync(mutate);
        error.ShouldBeOfType<EntrySharingRevocationUnavailableException>(error?.ToString());

        // Then
        await using (var verificationScope = apiFactory.Services.CreateAsyncScope())
        {
            var identity = verificationScope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
            var member = await identity.OrganizationMembers.Include(x => x.RoleAssignments).ThenInclude(x => x.Role)
                .SingleAsync(x => x.OrganizationId == seeded.OrganizationId && x.UserId == seeded.MemberId,
                    TestContext.Current.CancellationToken);
            member.Status.ShouldBe(OrganizationMemberStatus.Active);
            member.AuthorizationVersion.ShouldBe(1u);
            member.EffectivePermissions().ShouldBe(Permission.VaultManage | Permission.AuditView);
            var vault = verificationScope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
            var fence = await vault.EntryShareSenderAuthorities.SingleAsync(x => x.OrganizationId == seeded.OrganizationId
                && x.UserId == seeded.MemberId, TestContext.Current.CancellationToken);
            fence.RevokedThroughAuthorizationVersion.ShouldBe(commandWasCommitted ? 1u : 0u);
        }

        var retry = await ApplyChangeAsync(seeded, change);
        retry.StatusCode.ShouldBe(change == "removal" ? HttpStatusCode.NoContent : HttpStatusCode.OK);

        async Task<Response<MemberEntrySharingRevoked>> LoseAcknowledgementAsync(RevokeMemberEntrySharingCommand command)
        {
            if (commandWasCommitted)
            {
                await apiFactory.ConsumeAsync<Palladin.Module.Vault.Features.RevokeMemberEntrySharingConsumer,
                    RevokeMemberEntrySharingCommand>(command, TestContext.Current.CancellationToken);
            }

            throw new RequestTimeoutException("synthetic lost acknowledgement");
        }
    }

    [Theory]
    [InlineData("removal")]
    [InlineData("assignment")]
    [InlineData("definition")]
    public async Task When_IdentityRemovesSharingAuthority_Then_OldLinksAreBlockedBeforeTheResponse(string change)
    {
        // Given
        var seeded = await SeedShareAsync();

        // When
        var response = await ApplyChangeAsync(seeded, change);

        // Then
        response.StatusCode.ShouldBe(change == "removal" ? HttpStatusCode.NoContent : HttpStatusCode.OK);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var identity = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        var member = await identity.OrganizationMembers.SingleAsync(x => x.OrganizationId == seeded.OrganizationId
            && x.UserId == seeded.MemberId, TestContext.Current.CancellationToken);
        if (change == "removal")
        {
            member.Status.ShouldBe(OrganizationMemberStatus.Removing);
        }
        else
        {
            member.AuthorizationVersion.ShouldBe(2u);
        }

        var vault = scope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>();
        var fence = await vault.EntryShareSenderAuthorities.SingleAsync(x => x.OrganizationId == seeded.OrganizationId
            && x.UserId == seeded.MemberId, TestContext.Current.CancellationToken);
        fence.RevokedThroughAuthorizationVersion.ShouldBe(1u);
        var share = await vault.EntryShares.SingleAsync(x => x.Id == seeded.ShareId, TestContext.Current.CancellationToken);
        await Should.ThrowAsync<EntryShareUnavailableException>(() => scope.ServiceProvider.GetRequiredService<EntryShareAuthority>()
            .EnsureRecipientSourceAsync(share, TestContext.Current.CancellationToken));
        share.DeliveryCount.ShouldBe(0);
    }

    [Fact]
    public async Task When_SharingPermissionIsGrantedAgain_Then_OnlyNewAuthorizationCanCreateShares()
    {
        // Given
        var seeded = await SeedShareAsync();
        var revoked = await ApplyChangeAsync(seeded, "definition");
        revoked.StatusCode.ShouldBe(HttpStatusCode.OK);

        // When
        var restored = await seeded.OwnerClient.PUTAsync<UpdateOrganizationRoleEndpoint, UpdateOrganizationRoleRequest>(
            new UpdateOrganizationRoleRequest
            {
                RoleId = seeded.RoleId, Name = "Sharing sender", Permissions = (int)(Permission.VaultManage | Permission.AuditView),
            });

        // Then
        restored.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var member = await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>().OrganizationMembers
            .SingleAsync(x => x.OrganizationId == seeded.OrganizationId && x.UserId == seeded.MemberId,
                TestContext.Current.CancellationToken);
        member.AuthorizationVersion.ShouldBe(3u);
        var context = scope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>();
        var share = await context.EntryShares.SingleAsync(x => x.Id == seeded.ShareId, TestContext.Current.CancellationToken);
        var authority = scope.ServiceProvider.GetRequiredService<EntryShareAuthority>();
        await Should.ThrowAsync<EntryShareUnavailableException>(() =>
            authority.EnsureRecipientSourceAsync(share, TestContext.Current.CancellationToken));
        await authority.LoadSenderSourceAsync(new EntryScope(share.OrganizationId, share.VaultId, share.EntryId),
            seeded.MemberId, member.AuthorizationVersion, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task When_AnUnrelatedPermissionIsRemoved_Then_ExistingSharingAuthorityIsPreserved()
    {
        // Given
        var seeded = await SeedShareAsync();

        // When
        var response = await seeded.OwnerClient.PUTAsync<UpdateOrganizationRoleEndpoint, UpdateOrganizationRoleRequest>(
            new UpdateOrganizationRoleRequest
            {
                RoleId = seeded.RoleId,
                Name = "Sharing sender",
                Permissions = (int)Permission.VaultManage,
            });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>();
        var share = await context.EntryShares.SingleAsync(x => x.Id == seeded.ShareId, TestContext.Current.CancellationToken);
        await scope.ServiceProvider.GetRequiredService<EntryShareAuthority>()
            .EnsureRecipientSourceAsync(share, TestContext.Current.CancellationToken);
        var fence = await context.EntryShareSenderAuthorities.SingleAsync(x => x.OrganizationId == seeded.OrganizationId
            && x.UserId == seeded.MemberId, TestContext.Current.CancellationToken);
        fence.RevokedThroughAuthorizationVersion.ShouldBe(0u);
    }

    private static Task<HttpResponseMessage> ApplyChangeAsync(SharingSeed seeded, string change) => change switch
    {
        "removal" => seeded.OwnerClient.DELETEAsync<RemoveOrganizationMemberEndpoint, RemoveOrganizationMemberRequest>(
            new RemoveOrganizationMemberRequest { UserId = seeded.MemberId }),
        "assignment" => seeded.OwnerClient.PUTAsync<UpdateOrganizationMemberRolesEndpoint, UpdateOrganizationMemberRolesRequest>(
            new UpdateOrganizationMemberRolesRequest { UserId = seeded.MemberId, RoleIds = [seeded.WithoutAccessRoleId] }),
        _ => seeded.OwnerClient.PUTAsync<UpdateOrganizationRoleEndpoint, UpdateOrganizationRoleRequest>(
            new UpdateOrganizationRoleRequest
            {
                RoleId = seeded.RoleId, Name = "Sharing sender", Permissions = (int)Permission.AuditView,
            }),
    };

    private async Task<SharingSeed> SeedShareAsync()
    {
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var member = await apiFactory.Services.SeedAdditionalOrganizationMemberAsync(organization.Id);
        var now = Instant.FromUnixTimeMilliseconds(apiFactory.FakeClock.GetCurrentInstant().ToUnixTimeMilliseconds());
        Guid roleId;
        Guid withoutAccessRoleId;
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var identity = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
            var role = await identity.Roles.SingleAsync(x => x.OrganizationId == organization.Id && x.Name == "Test Member",
                TestContext.Current.CancellationToken);
            role.UpdateCustom("Sharing sender", Permission.VaultManage | Permission.AuditView, now);
            roleId = role.Id;
            var withoutAccess = Role.Create(Guid.NewGuid(), organization.Id, "Audit only", Permission.AuditView, false, now);
            identity.Roles.Add(withoutAccess);
            withoutAccessRoleId = withoutAccess.Id;
            await identity.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, member.Id);
        var entryId = await apiFactory.Services.SeedSyncEntryAsync(organization.Id, vault.Id, member.Id);
        await using var shareScope = apiFactory.Services.CreateAsyncScope();
        var context = shareScope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>();
        var entryScope = new EntryScope(organization.Id, vault.Id, entryId);
        var source = await shareScope.ServiceProvider.GetRequiredService<EntryShareAuthority>()
            .LoadSenderSourceAsync(entryScope, member.Id, 1, TestContext.Current.CancellationToken);
        var share = EntryShare.Create(Guid.NewGuid(), entryScope, source.Entry.CurrentRevision, member.Id,
            now, now + Duration.FromHours(1), 1, EntryShareRecipientMode.AnyoneWithLink, null,
            EntryShareProtection.None, null, new byte[32], new byte[24], new byte[16], false, 1, source.Member.AddedAt);
        context.Add(share);
        await context.CommitAsync(TestContext.Current.CancellationToken);
        return new SharingSeed(apiFactory.CreateAuthenticatedClient(owner, Permission.OrganizationManagement),
            organization.Id, member.Id, roleId, withoutAccessRoleId, share.Id);
    }

    private sealed record SharingSeed(HttpClient OwnerClient, Guid OrganizationId, Guid MemberId,
        Guid RoleId, Guid WithoutAccessRoleId, Guid ShareId);
}
