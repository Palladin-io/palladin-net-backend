using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Features;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Identity;

[Collection<ApiFactoryCollection>]
public sealed class SharedUnlockLinkTests(ApiFactory apiFactory) : TestBase
{
    private CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task When_DiscoveryCreatesLink_Then_ItStartsLockedWithoutChangingPreference()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user);
        var id = Guid.NewGuid();

        // When
        var created = await client.PostAsJsonAsync("api/account/shared-unlock/links",
            new CreateSharedUnlockLinkRequest { LinkId = id, ExpectedPreferenceRevision = 1 }, Cancellation);
        var fetched = await client.GetAsync($"api/account/shared-unlock/links/{id}", Cancellation);

        // Then
        created.StatusCode.ShouldBe(HttpStatusCode.OK);
        created.Headers.CacheControl!.NoStore.ShouldBeTrue();
        fetched.StatusCode.ShouldBe(HttpStatusCode.OK);
        fetched.Headers.CacheControl!.NoStore.ShouldBeTrue();
        (await fetched.Content.ReadFromJsonAsync<SharedUnlockLinkResponse>(Cancellation))
            .ShouldBe(new SharedUnlockLinkResponse(id, 1, 1, "locked", 0, 0));
        (await client.GetFromJsonAsync<SharedUnlockPreferenceResponse>("api/account/shared-unlock", Cancellation))
            .ShouldBe(new SharedUnlockPreferenceResponse(true, 1));
    }

    [Fact]
    public async Task When_DiscoveryRepeatsAfterRevocation_Then_ItCannotRecreateTheLink()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var link = SharedUnlockLink.Create(user.Id, Guid.NewGuid(), apiFactory.FakeClock.GetCurrentInstant());
        link.TryDisconnect(1, 1, apiFactory.FakeClock.GetCurrentInstant()).ShouldBeTrue();
        await SeedAsync(link);
        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var response = await client.PostAsJsonAsync("api/account/shared-unlock/links",
            new CreateSharedUnlockLinkRequest { LinkId = link.Id, ExpectedPreferenceRevision = 1 }, Cancellation);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await client.GetFromJsonAsync<SharedUnlockLinkResponse>($"api/account/shared-unlock/links/{link.Id}", Cancellation))
            .ShouldBe(new SharedUnlockLinkResponse(link.Id, 2, 2, "revoked", 1, 0));
    }

    [Theory]
    [InlineData(false, 1u)]
    [InlineData(true, 2u)]
    public async Task When_PreferenceIsDisabledOrStale_Then_DiscoveryCreatesNoLink(bool enabled, uint expectedRevision)
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync(UserFaker.Create().RuleFor(user => user.SharedUnlockEnabled, enabled));
        var client = apiFactory.CreateAuthenticatedClient(user);
        var id = Guid.NewGuid();

        // When
        var response = await client.PostAsJsonAsync("api/account/shared-unlock/links",
            new CreateSharedUnlockLinkRequest { LinkId = id, ExpectedPreferenceRevision = expectedRevision }, Cancellation);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await client.GetAsync($"api/account/shared-unlock/links/{id}", Cancellation)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task When_LockingAnActiveLink_Then_CurrentAndRepeatedLocksAdvanceTheEpoch()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var link = ActiveLink(user.Id);
        await SeedAsync(link);
        var client = apiFactory.CreateAuthenticatedClient(user);
        var route = $"api/account/shared-unlock/links/{link.Id}/lock";

        // When
        var locked = await client.PostAsJsonAsync(route, new LockSharedUnlockLinkRequest
        { LinkId = link.Id, ExpectedRevision = 2, ExpectedPreferenceRevision = 1 }, Cancellation);
        var stale = await client.PostAsJsonAsync(route, new LockSharedUnlockLinkRequest
        { LinkId = link.Id, ExpectedRevision = 2, ExpectedPreferenceRevision = 1 }, Cancellation);
        var repeated = await client.PostAsJsonAsync(route, new LockSharedUnlockLinkRequest
        { LinkId = link.Id, ExpectedRevision = 3, ExpectedPreferenceRevision = 1 }, Cancellation);

        // Then
        locked.StatusCode.ShouldBe(HttpStatusCode.OK);
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        repeated.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await repeated.Content.ReadFromJsonAsync<SharedUnlockLinkResponse>(Cancellation))
            .ShouldBe(new SharedUnlockLinkResponse(link.Id, 4, 4, "locked", 2, 0));
    }

    [Fact]
    public async Task When_OffWasSaved_Then_AQueuedLockDoesNotPropagate()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync(UserFaker.Create()
            .RuleFor(user => user.SharedUnlockEnabled, false).RuleFor(user => user.SharedUnlockRevision, 2u));
        var link = ActiveLink(user.Id);
        await SeedAsync(link);
        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var result = await client.PostAsJsonAsync($"api/account/shared-unlock/links/{link.Id}/lock",
            new LockSharedUnlockLinkRequest { LinkId = link.Id, ExpectedRevision = 2, ExpectedPreferenceRevision = 1 }, Cancellation);

        // Then
        result.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await client.GetFromJsonAsync<SharedUnlockLinkResponse>($"api/account/shared-unlock/links/{link.Id}", Cancellation))
            .ShouldBe(new SharedUnlockLinkResponse(link.Id, 2, 2, "active", 0, 0));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task When_DisconnectingThenReconnecting_Then_OtherLinksAndPreferenceStayIndependent(bool enabled)
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync(UserFaker.Create().RuleFor(user => user.SharedUnlockEnabled, enabled));
        var link = ActiveLink(user.Id);
        var other = ActiveLink(user.Id);
        await SeedAsync(link, other);
        var client = apiFactory.CreateAuthenticatedClient(user);
        var route = $"api/account/shared-unlock/links/{link.Id}";

        // When
        var disconnected = await client.PostAsJsonAsync($"{route}/disconnect", new DisconnectSharedUnlockLinkRequest
        { LinkId = link.Id, ExpectedRevision = 2 }, Cancellation);
        var staleReconnect = await client.PostAsJsonAsync($"{route}/reconnect", new ReconnectSharedUnlockLinkRequest
        { LinkId = link.Id, ExpectedRevision = 2 }, Cancellation);
        var reconnected = await client.PostAsJsonAsync($"{route}/reconnect", new ReconnectSharedUnlockLinkRequest
        { LinkId = link.Id, ExpectedRevision = 3 }, Cancellation);

        // Then
        disconnected.StatusCode.ShouldBe(HttpStatusCode.OK);
        staleReconnect.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        reconnected.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await reconnected.Content.ReadFromJsonAsync<SharedUnlockLinkResponse>(Cancellation))
            .ShouldBe(new SharedUnlockLinkResponse(link.Id, 4, 4, "locked", 2, 0));
        (await client.GetFromJsonAsync<SharedUnlockLinkResponse>($"api/account/shared-unlock/links/{other.Id}", Cancellation))
            .ShouldBe(new SharedUnlockLinkResponse(other.Id, 2, 2, "active", 0, 0));
        (await client.GetFromJsonAsync<SharedUnlockPreferenceResponse>("api/account/shared-unlock", Cancellation))
            .ShouldBe(new SharedUnlockPreferenceResponse(enabled, 1));
    }

    [Fact]
    public async Task When_OtherAccountKnowsLinkId_Then_ReadAndMutationsCannotReachIt()
    {
        // Given
        var (owner, _, _) = await apiFactory.Services.SeedUserAsync();
        var (other, _, _) = await apiFactory.Services.SeedUserAsync();
        var link = ActiveLink(owner.Id);
        await SeedAsync(link);
        var client = apiFactory.CreateAuthenticatedClient(other);
        var route = $"api/account/shared-unlock/links/{link.Id}";

        // When
        var read = await client.GetAsync(route, Cancellation);
        var locked = await client.PostAsJsonAsync($"{route}/lock", new { linkId = link.Id, expectedRevision = 2, expectedPreferenceRevision = 1, userId = owner.Id }, Cancellation);
        var logout = await client.PostAsJsonAsync($"{route}/logout", new { linkId = link.Id, expectedRevision = 2, expectedPreferenceRevision = 1, userId = owner.Id }, Cancellation);
        var disconnect = await client.PostAsJsonAsync($"{route}/disconnect", new { linkId = link.Id, expectedRevision = 2, userId = owner.Id }, Cancellation);
        var reconnect = await client.PostAsJsonAsync($"{route}/reconnect", new { linkId = link.Id, expectedRevision = 2, userId = owner.Id }, Cancellation);

        // Then
        new[] { read, locked, logout, disconnect, reconnect }.ShouldAllBe(response => response.StatusCode == HttpStatusCode.NotFound);
        var ownerClient = apiFactory.CreateAuthenticatedClient(owner);
        (await ownerClient.GetFromJsonAsync<SharedUnlockLinkResponse>(route, Cancellation))
            .ShouldBe(new SharedUnlockLinkResponse(link.Id, 2, 2, "active", 0, 0));
    }

    [Fact]
    public async Task When_LockWinsAConcurrentActivation_Then_DatabaseRejectsStaleEpochAtTheSameInstant()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var link = ActiveLink(user.Id);
        await SeedAsync(link);
        await using var first = apiFactory.Services.CreateAsyncScope();
        await using var second = apiFactory.Services.CreateAsyncScope();
        var firstContext = first.ServiceProvider.GetRequiredService<IdentityDomainWriteContext>();
        var secondContext = second.ServiceProvider.GetRequiredService<IdentityDomainWriteContext>();
        var locking = await firstContext.SharedUnlockLinks.SingleAsync(item => item.UserId == user.Id && item.Id == link.Id, Cancellation);
        var stale = await secondContext.SharedUnlockLinks.SingleAsync(item => item.UserId == user.Id && item.Id == link.Id, Cancellation);

        // When
        locking.TryLock(2, 1, apiFactory.FakeClock.GetCurrentInstant()).ShouldBeTrue();
        stale.TryActivateFromManualUnlock(2, 1, apiFactory.FakeClock.GetCurrentInstant()).ShouldBeTrue();
        await firstContext.CommitAsync(Cancellation);

        // Then
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => secondContext.CommitAsync(Cancellation));
        locking.AllowsTransfer(2).ShouldBeFalse();
        locking.AllowsTransfer(3).ShouldBeFalse();
    }

    [Fact]
    public async Task When_OffWinsAConcurrentDiscovery_Then_PreferenceFenceRollsBackTheLinkInsert()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        await using var first = apiFactory.Services.CreateAsyncScope();
        await using var second = apiFactory.Services.CreateAsyncScope();
        var offContext = first.ServiceProvider.GetRequiredService<IdentityDomainWriteContext>();
        var createContext = second.ServiceProvider.GetRequiredService<IdentityDomainWriteContext>();
        var off = await offContext.Users.SingleAsync(item => item.Id == user.Id, Cancellation);
        var observed = await createContext.Users.SingleAsync(item => item.Id == user.Id, Cancellation);
        var link = SharedUnlockLink.Create(user.Id, Guid.NewGuid(), apiFactory.FakeClock.GetCurrentInstant());
        createContext.Add(link);
        createContext.MarkPropertyAsUpdated(observed, current => current.SharedUnlockRevision);

        // When
        off.TrySetSharedUnlockPreference(false, 1, apiFactory.FakeClock.GetCurrentInstant()).ShouldBeTrue();
        await offContext.CommitAsync(Cancellation);

        // Then
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => createContext.CommitAsync(Cancellation));
        await using var verification = apiFactory.Services.CreateAsyncScope();
        (await verification.ServiceProvider.GetRequiredService<IdentityDomainReadContext>().SharedUnlockLinks
            .AnyAsync(item => item.UserId == user.Id && item.Id == link.Id, Cancellation)).ShouldBeFalse();
    }

    [Fact]
    public async Task When_Unauthenticated_Then_NoLinkEndpointAllowsAccess()
    {
        // Given
        var client = apiFactory.CreateClient();
        var id = Guid.NewGuid();
        var route = $"api/account/shared-unlock/links/{id}";

        // When
        var responses = new[]
        {
            await client.GetAsync(route, Cancellation),
            await client.PostAsJsonAsync("api/account/shared-unlock/links", new CreateSharedUnlockLinkRequest { LinkId = id, ExpectedPreferenceRevision = 1 }, Cancellation),
            await client.PostAsJsonAsync($"{route}/lock", new LockSharedUnlockLinkRequest { LinkId = id, ExpectedRevision = 1, ExpectedPreferenceRevision = 1 }, Cancellation),
            await client.PostAsJsonAsync($"{route}/logout", new LogoutSharedUnlockLinkRequest { LinkId = id, ExpectedRevision = 1, ExpectedPreferenceRevision = 1 }, Cancellation),
            await client.PostAsJsonAsync($"{route}/disconnect", new DisconnectSharedUnlockLinkRequest { LinkId = id, ExpectedRevision = 1 }, Cancellation),
            await client.PostAsJsonAsync($"{route}/reconnect", new ReconnectSharedUnlockLinkRequest { LinkId = id, ExpectedRevision = 1 }, Cancellation),
        };

        // Then
        responses.ShouldAllBe(response => response.StatusCode == HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task When_SelectedMembershipIsRemoving_Then_OnlyClosingActionsRemainAvailable()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var link = ActiveLink(user.Id);
        await SeedAsync(link);
        var client = apiFactory.CreateAuthenticatedClient(user);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
        await database.OrganizationMembers.Where(member => member.UserId == user.Id && member.OrganizationId == user.OrganizationId)
            .ExecuteUpdateAsync(update => update.SetProperty(member => member.Status,
                OrganizationMemberStatus.Removing), Cancellation);
        var route = $"api/account/shared-unlock/links/{link.Id}";

        // When
        var locked = await client.PostAsJsonAsync($"{route}/lock", new LockSharedUnlockLinkRequest
        { LinkId = link.Id, ExpectedRevision = 2, ExpectedPreferenceRevision = 1 }, Cancellation);
        var disconnected = await client.PostAsJsonAsync($"{route}/disconnect", new DisconnectSharedUnlockLinkRequest
        { LinkId = link.Id, ExpectedRevision = 3 }, Cancellation);
        var reconnect = await client.PostAsJsonAsync($"{route}/reconnect", new ReconnectSharedUnlockLinkRequest
        { LinkId = link.Id, ExpectedRevision = 4 }, Cancellation);
        var create = await client.PostAsJsonAsync("api/account/shared-unlock/links", new CreateSharedUnlockLinkRequest
        { LinkId = Guid.NewGuid(), ExpectedPreferenceRevision = 1 }, Cancellation);

        // Then
        locked.StatusCode.ShouldBe(HttpStatusCode.OK);
        disconnected.StatusCode.ShouldBe(HttpStatusCode.OK);
        reconnect.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        create.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.GetFromJsonAsync<SharedUnlockLinkResponse>(route, Cancellation))
            .ShouldBe(new SharedUnlockLinkResponse(link.Id, 4, 4, "revoked", 2, 0));
    }

    private SharedUnlockLink ActiveLink(Guid userId)
    {
        var link = SharedUnlockLink.Create(userId, Guid.NewGuid(), apiFactory.FakeClock.GetCurrentInstant());
        link.TryActivateFromManualUnlock(1, 1, apiFactory.FakeClock.GetCurrentInstant()).ShouldBeTrue();
        return link;
    }

    private async Task SeedAsync(params SharedUnlockLink[] links)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
        context.SharedUnlockLinks.AddRange(links);
        await context.SaveChangesAsync(Cancellation);
    }
}
