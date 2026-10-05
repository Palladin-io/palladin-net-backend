using System.Net;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Palladin.Core.Types;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault;

[Collection<ApiFactoryCollection>]
public sealed class MyEntrySharesTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_ListingOwnShares_Then_PagesCrossVaultsWithoutForeignSharesOrDeliveryMaterial()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var first = await SeedShareAsync(organization.Id, user.Id);
        var second = await SeedShareAsync(organization.Id, user.Id);
        var colleague = await apiFactory.Services.SeedAdditionalOrganizationMemberAsync(organization.Id);
        await SeedShareAsync(organization.Id, colleague.Id);
        var (other, foreignOrganization, _) = await apiFactory.Services.SeedUserAsync();
        await SeedShareAsync(foreignOrganization.Id, other.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var page = await client.GETAsync<ListMyEntrySharesEndpoint, ListMyEntrySharesRequest, ListMyEntrySharesResponse>(
            new ListMyEntrySharesRequest { PageSize = 1 });
        var next = await client.GETAsync<ListMyEntrySharesEndpoint, ListMyEntrySharesRequest, ListMyEntrySharesResponse>(
            new ListMyEntrySharesRequest { PageSize = 1, Cursor = page.Result.NextCursor });

        // Then
        page.Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        page.Response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        page.Result.Items.Count.ShouldBe(1);
        page.Result.Items[0].Share.Status.ShouldBe("active");
        page.Result.NextCursor.ShouldNotBeNull();
        next.Result.Items.Count.ShouldBe(1);
        next.Result.NextCursor.ShouldBeNull();
        new[] { page.Result.Items[0].Share.ShareId, next.Result.Items[0].Share.ShareId }
            .ShouldBe(new[] { first.Id, second.Id }, ignoreOrder: true);
        var json = await page.Response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        json.ShouldNotContain("ciphertext", Case.Insensitive);
        json.ShouldNotContain("accessToken", Case.Insensitive);
        json.ShouldNotContain("nonce", Case.Insensitive);
    }

    [Fact]
    public async Task When_MembershipIsRemoved_Then_OwnSharesAreNotVisible()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var share = await SeedShareAsync(organization.Id, user.Id);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
        await db.VaultMembers.Where(x => x.OrganizationId == organization.Id && x.VaultId == share.VaultId && x.UserId == user.Id)
            .ExecuteDeleteAsync(TestContext.Current.CancellationToken);

        // When
        var page = await apiFactory.CreateAuthenticatedClient(user)
            .GETAsync<ListMyEntrySharesEndpoint, ListMyEntrySharesRequest, ListMyEntrySharesResponse>(new());

        // Then
        page.Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        page.Result.Items.ShouldBeEmpty();
    }

    private async Task<EntryShare> SeedShareAsync(Guid organizationId, Guid userId)
    {
        var vault = await apiFactory.Services.SeedVaultAsync(organizationId, userId);
        var entryId = await apiFactory.Services.SeedSyncEntryAsync(organizationId, vault.Id, userId);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
        var membership = await db.VaultMembers.SingleAsync(x => x.OrganizationId == organizationId && x.VaultId == vault.Id && x.UserId == userId,
            TestContext.Current.CancellationToken);
        var now = apiFactory.FakeClock.GetCurrentInstant();
        var share = EntryShare.Create(Guid.NewGuid(), new EntryScope(organizationId, vault.Id, entryId), new EntryRevision(1),
            userId, now, now + Duration.FromHours(1), null, EntryShareRecipientMode.AnyoneWithLink, null,
            EntryShareProtection.None, null, new byte[32], new byte[24], new byte[32], false, 1, membership.AddedAt);
        if (!await db.EntryShareSenderAuthorities.AnyAsync(x => x.OrganizationId == organizationId && x.UserId == userId, TestContext.Current.CancellationToken))
        {
            db.EntryShareSenderAuthorities.Add(EntryShareSenderAuthority.Create(organizationId, userId));
        }
        db.EntryShares.Add(share);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return share;
    }
}
