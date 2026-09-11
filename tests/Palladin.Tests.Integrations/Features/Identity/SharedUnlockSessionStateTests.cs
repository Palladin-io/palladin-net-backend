using System.Net;
using System.Text.Json;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Features;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Identity;

public sealed partial class SharedUnlockSessionLifecycleTests
{
    [Fact]
    public async Task When_StaleJwtAndRefreshSurviveMembershipRevocation_Then_JwtAuthenticationRejectsClosingRead()
    {
        // Given
        var account = await SeedAsync();
        var client = apiFactory.CreateAuthenticatedClient(account.User);
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
            var membership = await db.OrganizationMembers.SingleAsync(member =>
                member.UserId == account.User.Id && member.OrganizationId == account.User.OrganizationId, Ct);
            membership.InvalidateAuthorization(Now);
            membership.FetchEvents();
            await db.SaveChangesAsync(Ct);
        }

        // When
        var (response, _) = await client.POSTAsync<GetSharedUnlockSessionStateEndpoint,
            GetSharedUnlockSessionStateRequest, SharedUnlockSessionStateResponse>(new()
            { RefreshToken = account.PeerRaw, LinkId = account.LinkId });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await using var verification = apiFactory.Services.CreateAsyncScope();
        var hash = TokenService.HashToken(account.PeerRaw);
        var token = await verification.ServiceProvider.GetRequiredService<IdentityDbReadContext>().RefreshTokens
            .SingleAsync(token => token.UserId == account.User.Id && token.TokenHash == hash, Ct);
        token.AuthorizationVersion.ShouldBe(1u);
        token.RevokedAt.ShouldBeNull();
    }

    [Theory]
    [InlineData("none")]
    [InlineData("lock")]
    [InlineData("disconnect")]
    [InlineData("logout")]
    public async Task When_OwnSessionQueriesClosingState_Then_OnlyItsBoundLinkDecides(string closing)
    {
        // Given
        var account = await SeedAsync();
        if (closing != "none")
        {
            await CloseForStateAsync(account, closing);
        }
        var before = await StateSnapshotAsync(account);

        // When
        var (response, result) = await ReadStateAsync(account, account.PeerRaw, account.LinkId);
        var (_, other) = await ReadStateAsync(account, account.OtherRaw, account.LinkId);
        var after = await StateSnapshotAsync(account);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        result.Action.ShouldBe(closing == "disconnect" ? "lock" : closing);
        result.Link!.LinkId.ShouldBe(account.LinkId);
        result.Link.State.ShouldBe(closing == "none" ? "active" : closing == "disconnect" ? "revoked" : "locked");
        other.Action.ShouldBe("none");
        after.ShouldBe(before);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        body.RootElement.EnumerateObject().Select(property => property.Name).ShouldBe(["action", "link"]);
        body.RootElement.GetProperty("link").EnumerateObject().Select(property => property.Name)
            .ShouldBe(["linkId", "revision", "epoch", "state", "lastInvalidationSequence", "lastLogoutSequence"]);
    }

    [Fact]
    public async Task When_FreshIndependentSessionHasOldLocalMarker_Then_OldLogoutDoesNotCloseIt()
    {
        // Given
        var account = await SeedAsync();
        await CloseForStateAsync(account, "logout");
        var fresh = Guid.NewGuid().ToString("N");
        await apiFactory.Services.SeedRefreshTokenAsync(account.User.Id, fresh);

        // When
        var (response, result) = await ReadStateAsync(account, fresh, account.LinkId);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.Action.ShouldBe("none");
        result.Link!.LastLogoutSequence.ShouldBe(3u);
    }

    [Fact]
    public async Task When_RefreshRotatesAfterLock_Then_CurrentTokenReadsSameLogicalSessionAndOldTokenFails()
    {
        // Given
        var account = await SeedAsync();
        await CloseForStateAsync(account, "lock");
        var rotated = await RefreshAsync(account.PeerRaw);
        rotated.Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // When
        var (current, result) = await ReadStateAsync(account, rotated.Result.RefreshToken, account.LinkId);
        var (old, _) = await ReadStateAsync(account, account.PeerRaw, account.LinkId);

        // Then
        current.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.Action.ShouldBe("lock");
        old.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task When_AnotherAccountOrLinkIsPresented_Then_ItCannotSelectForeignSessionAuthority()
    {
        // Given
        var account = await SeedAsync();
        var other = await SeedAsync();
        await CloseForStateAsync(other, "logout");

        // When
        var (foreignToken, _) = await ReadStateAsync(account, other.PeerRaw, other.LinkId);
        var (foreignLink, result) = await ReadStateAsync(account, account.PeerRaw, other.LinkId);
        var (missing, _) = await ReadStateAsync(account, "synthetic-unknown-refresh", account.LinkId);
        var (anonymous, _) = await apiFactory.CreateClient().POSTAsync<GetSharedUnlockSessionStateEndpoint,
            GetSharedUnlockSessionStateRequest, SharedUnlockSessionStateResponse>(new()
            { RefreshToken = account.PeerRaw, LinkId = account.LinkId });

        // Then
        foreignToken.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        foreignLink.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.Action.ShouldBe("none");
        result.Link.ShouldBeNull();
        missing.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("revoked")]
    [InlineData("organization")]
    [InlineData("authorization-version")]
    public async Task When_OwnTokenIsNoLongerCurrent_Then_ReadCannotBorrowAnotherSession(string stale)
    {
        // Given
        var account = await SeedAsync();
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
            var hash = TokenService.HashToken(account.PeerRaw);
            var token = await db.RefreshTokens.SingleAsync(token => token.UserId == account.User.Id && token.TokenHash == hash, Ct);
            if (stale == "expired") { db.Entry(token).Property(token => token.ExpiresAt).CurrentValue = Now; }
            if (stale == "revoked") { token.Revoke(Now); }
            if (stale == "organization") { db.Entry(token).Property(token => token.OrganizationId).CurrentValue = Guid.NewGuid(); }
            if (stale == "authorization-version") { db.Entry(token).Property(token => token.AuthorizationVersion).CurrentValue = 999; }
            await db.SaveChangesAsync(Ct);
        }

        // When
        var (response, _) = await ReadStateAsync(account, account.PeerRaw, account.LinkId);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task When_SharingOffAndMembershipRemoving_Then_ExistingLogoutStillRepairsWithoutUnlockRoot()
    {
        // Given
        var account = await SeedAsync();
        await CloseForStateAsync(account, "logout");
        await SetPreferenceAsync(account.User.Id, false, 1);
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
            var member = await db.OrganizationMembers.SingleAsync(member => member.UserId == account.User.Id, Ct);
            db.Entry(member).Property(member => member.Status).CurrentValue = OrganizationMemberStatus.Removing;
            await db.SaveChangesAsync(Ct);
        }

        // When
        var (response, result) = await ReadStateAsync(account, account.PeerRaw, account.LinkId);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.Action.ShouldBe("logout");
    }

    [Fact]
    public async Task When_LinkReactivatedWithFreshOwnRoot_Then_OldAndNewSessionsHaveDifferentClosingActions()
    {
        // Given
        var account = await SeedAsync();
        await CloseForStateAsync(account, "disconnect");
        await CloseForStateAsync(account, "logout");
        var freshRaw = Guid.NewGuid().ToString("N");
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
            var user = await db.Users.SingleAsync(user => user.Id == account.User.Id, Ct);
            var link = await db.SharedUnlockLinks.SingleAsync(link => link.UserId == user.Id && link.Id == account.LinkId, Ct);
            user.TryAdvanceSharedUnlockSequence().ShouldBeTrue();
            link.TryReconnect(link.Revision, user.SharedUnlockSequence, Now).ShouldBeTrue();
            user.TryAdvanceSharedUnlockSequence().ShouldBeTrue();
            var fresh = RefreshToken.Create(Guid.NewGuid(), user.Id, user.OrganizationId, TokenService.HashToken(freshRaw),
                1, Now + Duration.FromDays(1), Now);
            var root = SharedUnlockAuthorization.Create(user.Id, fresh.SessionId!.Value);
            root.AuthorizeManualUnlock(Guid.NewGuid(), user, fresh, Generation, Now + Duration.FromMinutes(15),
                Now + Duration.FromHours(8), Now + Duration.FromHours(1), Now);
            link.TryActivateFromManualUnlock(link.Revision, user.SharedUnlockSequence, Now).ShouldBeTrue();
            root.TryBind(link).ShouldBeTrue();
            db.RefreshTokens.Add(fresh);
            db.SharedUnlockAuthorizations.Add(root);
            await db.SaveChangesAsync(Ct);
        }

        // When
        var (oldResponse, oldState) = await ReadStateAsync(account, account.PeerRaw, account.LinkId);
        var (freshResponse, freshState) = await ReadStateAsync(account, freshRaw, account.LinkId);

        // Then
        oldResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        freshResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        oldState.Action.ShouldBe("logout");
        freshState.Action.ShouldBe("none");
        oldState.Link.ShouldBe(freshState.Link);
        freshState.Link!.State.ShouldBe("active");
    }

    [Fact]
    public async Task When_BoundLinkIsMissing_Then_OnlyTheBoundOwnSessionMustLogOut()
    {
        // Given
        var account = await SeedAsync();
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>().SharedUnlockLinks
                .Where(link => link.UserId == account.User.Id && link.Id == account.LinkId).ExecuteDeleteAsync(Ct);
        }

        // When
        var (response, own) = await ReadStateAsync(account, account.PeerRaw, account.LinkId);
        var (_, other) = await ReadStateAsync(account, account.OtherRaw, account.LinkId);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        own.Action.ShouldBe("logout");
        own.Link.ShouldBeNull();
        other.Action.ShouldBe("none");
    }

    private async Task CloseForStateAsync(Account account, string action)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
        var user = await db.Users.SingleAsync(user => user.Id == account.User.Id, Ct);
        var link = await db.SharedUnlockLinks.SingleAsync(link => link.UserId == user.Id && link.Id == account.LinkId, Ct);
        user.TryAdvanceSharedUnlockSequence().ShouldBeTrue();
        (action switch
        {
            "lock" => link.TryLock(link.Revision, user.SharedUnlockSequence, Now),
            "disconnect" => link.TryDisconnect(link.Revision, user.SharedUnlockSequence, Now),
            "logout" => link.TryLogout(link.Revision, user.SharedUnlockSequence, Now),
            _ => false,
        }).ShouldBeTrue();
        await db.SaveChangesAsync(Ct);
    }

    private Task<TestResult<SharedUnlockSessionStateResponse>> ReadStateAsync(Account account, string refreshToken, Guid linkId) =>
        apiFactory.CreateAuthenticatedClient(account.User).POSTAsync<GetSharedUnlockSessionStateEndpoint,
            GetSharedUnlockSessionStateRequest, SharedUnlockSessionStateResponse>(new() { RefreshToken = refreshToken, LinkId = linkId });

    private async Task<string> StateSnapshotAsync(Account account)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        return JsonSerializer.Serialize(new
        {
            User = await db.Users.Where(user => user.Id == account.User.Id)
                .Select(user => new { user.SharedUnlockSequence, user.SharedUnlockRevision }).SingleAsync(Ct),
            Roots = await db.SharedUnlockAuthorizations.Where(root => root.UserId == account.User.Id).OrderBy(root => root.SessionId)
                .Select(root => new { root.Id, root.Sequence, root.LinkId, root.LinkEpoch,
                    UnlockedAt = root.UnlockedAt.ToUnixTimeMilliseconds(), Idle = root.IdleDeadline.ToUnixTimeMilliseconds(),
                    Absolute = root.AbsoluteDeadline.ToUnixTimeMilliseconds(), Offline = root.OfflineDeadline.ToUnixTimeMilliseconds() }).ToListAsync(Ct),
            Sessions = await db.RefreshTokens.Where(token => token.UserId == account.User.Id).OrderBy(token => token.Id)
                .Select(token => new { token.Id, token.SessionId, RevokedAt = token.RevokedAt == null ? (long?)null : token.RevokedAt.Value.ToUnixTimeMilliseconds(),
                    ExpiresAt = token.ExpiresAt.ToUnixTimeMilliseconds(), token.ReplacedByTokenId }).ToListAsync(Ct),
            Links = await db.SharedUnlockLinks.Where(link => link.UserId == account.User.Id).OrderBy(link => link.Id)
                .Select(link => new { link.Id, link.Epoch, link.Revision, link.State, link.LastInvalidationSequence, link.LastLogoutSequence }).ToListAsync(Ct),
        });
    }
}
