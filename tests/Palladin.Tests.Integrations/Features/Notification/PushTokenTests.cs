using System.Net;
using Palladin.Core.Types;
using Palladin.Module.Notification.Features;
using Palladin.Module.Notification.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Seeders;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Notification;

[Collection<ApiFactoryCollection>]
public sealed class PushTokenTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_RegisteringNewToken_Then_PersistsWithOrganization()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var (response, result) = await client
            .POSTAsync<RegisterPushTokenEndpoint, RegisterPushTokenRequest, RegisterPushTokenResponse>(
                new RegisterPushTokenRequest { Token = "fcm-token-1", Platform = PushPlatform.Web, DeviceName = "Chrome" });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<NotificationDbReadContext>();
        var persisted = await readContext.PushTokens.FirstAsync(t => t.Id == result!.Id);
        persisted.UserId.ShouldBe(user.Id);
        persisted.OrganizationId.ShouldBe(organization.Id);
        persisted.Platform.ShouldBe(PushPlatform.Web);
    }

    [Fact]
    public async Task When_RegisteringSameTokenTwice_Then_UpsertsSingleRow()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user);
        var token = $"fcm-dup-{Guid.NewGuid()}";

        // When — same token, refreshed device name
        await client.POSTAsync<RegisterPushTokenEndpoint, RegisterPushTokenRequest, RegisterPushTokenResponse>(
            new RegisterPushTokenRequest { Token = token, Platform = PushPlatform.Android, DeviceName = "Old" });
        await client.POSTAsync<RegisterPushTokenEndpoint, RegisterPushTokenRequest, RegisterPushTokenResponse>(
            new RegisterPushTokenRequest { Token = token, Platform = PushPlatform.Android, DeviceName = "New" });

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<NotificationDbReadContext>();
        var rows = await readContext.PushTokens.Where(t => t.Token == token).ToListAsync();
        rows.Count.ShouldBe(1);
        rows[0].DeviceName.ShouldBe("New");
    }

    [Fact]
    public async Task When_SameTokenRegisteredByAnotherUser_Then_ReassignedToNewOwner()
    {
        // Given — device token first owned by user A
        var (userA, _, _) = await apiFactory.Services.SeedUserAsync();
        var clientA = apiFactory.CreateAuthenticatedClient(userA);
        var token = $"fcm-handoff-{Guid.NewGuid()}";
        await clientA.POSTAsync<RegisterPushTokenEndpoint, RegisterPushTokenRequest, RegisterPushTokenResponse>(
            new RegisterPushTokenRequest { Token = token, Platform = PushPlatform.Ios, DeviceName = "Shared device" });

        // When — same physical device (same token) re-registered by user B in another organization
        var (userB, orgB, _) = await apiFactory.Services.SeedUserAsync();
        var clientB = apiFactory.CreateAuthenticatedClient(userB);
        await clientB.POSTAsync<RegisterPushTokenEndpoint, RegisterPushTokenRequest, RegisterPushTokenResponse>(
            new RegisterPushTokenRequest { Token = token, Platform = PushPlatform.Ios, DeviceName = "Shared device" });

        // Then — single row, now owned by B (A no longer receives push for this device)
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<NotificationDbReadContext>();
        var rows = await readContext.PushTokens.Where(t => t.Token == token).ToListAsync();
        rows.Count.ShouldBe(1);
        rows[0].UserId.ShouldBe(userB.Id);
        rows[0].OrganizationId.ShouldBe(orgB.Id);
    }

    [Fact]
    public async Task When_ListingTokens_Then_ReturnsMetadataWithoutTokenValue()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user);
        await client.POSTAsync<RegisterPushTokenEndpoint, RegisterPushTokenRequest, RegisterPushTokenResponse>(
            new RegisterPushTokenRequest { Token = "secret-token-value", Platform = PushPlatform.Ios, DeviceName = "iPhone" });

        // When
        var response = await client.GetAsync("api/push-tokens");

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldNotContain("secret-token-value");
        body.ShouldContain("iPhone");
    }

    [Fact]
    public async Task When_RemovingOwnToken_Then_Returns204AndDeletes()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user);
        var (_, registered) = await client
            .POSTAsync<RegisterPushTokenEndpoint, RegisterPushTokenRequest, RegisterPushTokenResponse>(
                new RegisterPushTokenRequest { Token = "to-remove", Platform = PushPlatform.Web });

        // When
        var response = await client.DeleteAsync($"api/push-tokens/{registered!.Id}");

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<NotificationDbReadContext>();
        (await readContext.PushTokens.AnyAsync(t => t.Id == registered.Id)).ShouldBeFalse();
    }

    [Fact]
    public async Task When_RemovingAnotherUsersToken_Then_Returns404()
    {
        // Given
        var (owner, _, _) = await apiFactory.Services.SeedUserAsync();
        var ownerClient = apiFactory.CreateAuthenticatedClient(owner);
        var (_, registered) = await ownerClient
            .POSTAsync<RegisterPushTokenEndpoint, RegisterPushTokenRequest, RegisterPushTokenResponse>(
                new RegisterPushTokenRequest { Token = "owned", Platform = PushPlatform.Web });

        var (outsider, _, _) = await apiFactory.Services.SeedUserAsync();
        var outsiderClient = apiFactory.CreateAuthenticatedClient(outsider);

        // When
        var response = await outsiderClient.DeleteAsync($"api/push-tokens/{registered!.Id}");

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
