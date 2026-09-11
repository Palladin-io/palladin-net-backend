using System.Net;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NSubstitute;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Features;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Infrastructure.SharedUnlock;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Identity;

[Collection<ApiFactoryCollection>]
public sealed class SharedUnlockSessionLifecycleTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_LinkedClientsLogOut_Then_TheirSessionsStayRevokedWhileOtherDevicesAndAccountsSurvive()
    {
        // Given
        var account = await SeedAsync();
        var other = await SeedAsync();
        var client = apiFactory.CreateAuthenticatedClient(account.User);

        // When
        var (response, result) = await client.POSTAsync<LogoutSharedUnlockLinkEndpoint,
            LogoutSharedUnlockLinkRequest, SharedUnlockLinkResponse>(Request(account));
        var source = await RefreshAsync(account.SourceRaw);
        var peer = await RefreshAsync(account.PeerRaw);
        var otherDevice = await RefreshAsync(account.OtherRaw);
        var otherAccount = await RefreshAsync(other.SourceRaw);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        result.State.ShouldBe("locked");
        result.LastLogoutSequence.ShouldBe(3u);
        result.LastInvalidationSequence.ShouldBe(3u);
        source.Response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        peer.Response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        otherDevice.Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        otherAccount.Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        (await db.SharedUnlockLinks.SingleAsync(l => l.UserId == account.User.Id && l.Id == account.OtherLinkId, Ct))
            .State.ShouldBe(SharedUnlockLinkState.Active);
    }

    [Fact]
    public async Task When_OffPrecedesLogout_Then_SharedLogoutIsRejectedAndOrdinaryLogoutStaysLocal()
    {
        // Given
        var account = await SeedAsync();
        await SetPreferenceAsync(account.User.Id, false, 1);
        var client = apiFactory.CreateAuthenticatedClient(account.User);

        // When
        var (shared, _) = await client.POSTAsync<LogoutSharedUnlockLinkEndpoint,
            LogoutSharedUnlockLinkRequest, SharedUnlockLinkResponse>(Request(account));
        var local = await client.POSTAsync<LogoutEndpoint, LogoutRequest>(new() { RefreshToken = account.SourceRaw });
        var source = await RefreshAsync(account.SourceRaw);
        var peer = await RefreshAsync(account.PeerRaw);

        // Then
        shared.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        local.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        source.Response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        peer.Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var link = await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>().SharedUnlockLinks
            .SingleAsync(l => l.UserId == account.User.Id && l.Id == account.LinkId, Ct);
        link.LastLogoutSequence.ShouldBe(0u);
        link.State.ShouldBe(SharedUnlockLinkState.Active);
    }

    [Fact]
    public async Task When_PreferenceChangesAfterLogout_Then_OldRefreshAndFreshPasswordCannotReviveThatSession()
    {
        // Given
        var account = await SeedAsync();
        var client = apiFactory.CreateAuthenticatedClient(account.User);
        var (loggedOut, _) = await client.POSTAsync<LogoutSharedUnlockLinkEndpoint,
            LogoutSharedUnlockLinkRequest, SharedUnlockLinkResponse>(Request(account));
        loggedOut.StatusCode.ShouldBe(HttpStatusCode.OK);
        await SetPreferenceAsync(account.User.Id, false, 1);
        await SetPreferenceAsync(account.User.Id, true, 2);

        // When
        var refresh = await RefreshAsync(account.SourceRaw);
        var (authorized, _) = await client.POSTAsync<AuthorizeSharedUnlockEndpoint,
            AuthorizeSharedUnlockRequest, AuthorizeSharedUnlockResponse>(new()
        {
            RefreshToken = account.SourceRaw, AuthCredential = Credential, SourceGeneration = Generation,
            ExpectedPreferenceRevision = 3, ExpectedCredentialRevision = 1, ExpectedPrivateKeyWrapRevision = 1,
            IdleDeadlineMs = (Now + Duration.FromMinutes(15)).ToUnixTimeMilliseconds(),
            AbsoluteDeadlineMs = (Now + Duration.FromHours(8)).ToUnixTimeMilliseconds(),
            OfflineDeadlineMs = (Now + Duration.FromHours(1)).ToUnixTimeMilliseconds(),
        });

        // Then
        refresh.Response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        authorized.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        (await db.SharedUnlockAuthorizations.Where(a => a.UserId == account.User.Id && a.LinkId == account.LinkId)
            .Select(a => a.Sequence).ToListAsync(Ct)).ShouldAllBe(sequence => sequence == 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task When_LogoutFollowsClosingWithRemovingMembership_Then_ExistingLocalSessionsStillClose(bool disconnected)
    {
        // Given
        var account = await SeedAsync();
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
            var user = await db.Users.SingleAsync(u => u.Id == account.User.Id, Ct);
            var link = await db.SharedUnlockLinks.SingleAsync(l => l.UserId == user.Id && l.Id == account.LinkId, Ct);
            user.TryAdvanceSharedUnlockSequence().ShouldBeTrue();
            (disconnected ? link.TryDisconnect(2, user.SharedUnlockSequence, Now)
                : link.TryLock(2, user.SharedUnlockSequence, Now)).ShouldBeTrue();
            var member = await db.OrganizationMembers.SingleAsync(m => m.UserId == user.Id, Ct);
            db.Entry(member).Property(m => m.Status).CurrentValue = OrganizationMemberStatus.Removing;
            await db.SaveChangesAsync(Ct);
        }

        // When
        var (response, result) = await apiFactory.CreateAuthenticatedClient(account.User)
            .POSTAsync<LogoutSharedUnlockLinkEndpoint, LogoutSharedUnlockLinkRequest, SharedUnlockLinkResponse>(
                Request(account) with { ExpectedRevision = 3 });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.LastLogoutSequence.ShouldBe(4u);
        result.State.ShouldBe(disconnected ? "revoked" : "locked");
        (await RefreshAsync(account.PeerRaw)).Response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task When_LogoutFollowsDisconnect_Then_ReconnectCannotReviveItsSessions()
    {
        // Given
        var account = await SeedAsync();
        var client = apiFactory.CreateAuthenticatedClient(account.User);
        var (disconnected, link) = await client.POSTAsync<DisconnectSharedUnlockLinkEndpoint,
            DisconnectSharedUnlockLinkRequest, SharedUnlockLinkResponse>(new()
        { LinkId = account.LinkId, ExpectedRevision = 2 });
        disconnected.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RefreshAsync(account.PeerRaw)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // When
        var (stale, _) = await client.POSTAsync<LogoutSharedUnlockLinkEndpoint,
            LogoutSharedUnlockLinkRequest, SharedUnlockLinkResponse>(Request(account));
        var (response, loggedOut) = await client.POSTAsync<LogoutSharedUnlockLinkEndpoint,
            LogoutSharedUnlockLinkRequest, SharedUnlockLinkResponse>(Request(account) with { ExpectedRevision = link.Revision });
        var (reconnected, reopened) = await client.POSTAsync<ReconnectSharedUnlockLinkEndpoint,
            ReconnectSharedUnlockLinkRequest, SharedUnlockLinkResponse>(new()
        { LinkId = account.LinkId, ExpectedRevision = loggedOut.Revision });

        // Then
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        loggedOut.State.ShouldBe("revoked");
        loggedOut.Revision.ShouldBe(link.Revision + 1);
        loggedOut.Epoch.ShouldBe(link.Epoch + 1);
        loggedOut.LastLogoutSequence.ShouldBeGreaterThan(link.LastInvalidationSequence);
        reconnected.StatusCode.ShouldBe(HttpStatusCode.OK);
        reopened.State.ShouldBe("locked");
        reopened.LastLogoutSequence.ShouldBe(loggedOut.LastLogoutSequence);
        (await RefreshAsync(account.SourceRaw)).Response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await RefreshAsync(account.PeerRaw)).Response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await RefreshAsync(account.OtherRaw)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task When_LogoutWinsAfterRefreshPreparation_Then_RotationAndTokenIssuanceCannotCommit()
    {
        // Given
        var account = await SeedAsync();
        await using var attemptScope = apiFactory.Services.CreateAsyncScope();
        var attempt = attemptScope.ServiceProvider.GetRequiredService<IdentityDomainWriteContext>();
        var user = await attempt.Users.SingleAsync(u => u.Id == account.User.Id, Ct);
        var hash = TokenService.HashToken(account.PeerRaw);
        var session = await attempt.RefreshTokens.SingleAsync(t => t.UserId == user.Id && t.TokenHash == hash, Ct);
        var revocation = await SharedUnlockSessionRevocation.LoadAsync(attempt, session, Ct);
        revocation.IsRevoked.ShouldBeFalse();
        revocation.Fence(attempt);
        attempt.MarkPropertyAsUpdated(user, u => u.SharedUnlockSequence);
        var newId = Guid.NewGuid();
        session.Revoke(Now, newId);
        attempt.Add(RefreshToken.Create(newId, user.Id, session.OrganizationId, "synthetic-new-hash", 1,
            Now + Duration.FromDays(365), Now, sessionId: session.SessionId));

        // When
        var (response, _) = await apiFactory.CreateAuthenticatedClient(account.User)
            .POSTAsync<LogoutSharedUnlockLinkEndpoint, LogoutSharedUnlockLinkRequest, SharedUnlockLinkResponse>(Request(account));

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => attempt.CommitAsync(Ct));
        await using var scope = apiFactory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>().RefreshTokens.AnyAsync(t => t.Id == newId, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task When_LinkedSessionRotatesBeforeLogout_Then_TheSuccessorRemainsInTheRevokedFamily()
    {
        // Given
        var account = await SeedAsync();
        var rotated = await RefreshAsync(account.PeerRaw);
        rotated.Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        rotated.Result.RefreshToken.ShouldNotBe(account.PeerRaw);

        // When
        var (response, _) = await apiFactory.CreateAuthenticatedClient(account.User)
            .POSTAsync<LogoutSharedUnlockLinkEndpoint, LogoutSharedUnlockLinkRequest, SharedUnlockLinkResponse>(Request(account));
        var late = await RefreshAsync(rotated.Result.RefreshToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        late.Response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await RefreshAsync(account.OtherRaw)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task When_RealActivityAdvancesOwnIdle_Then_HardLimitsAndOtherClientStayUnchanged(bool sharingEnabled)
    {
        // Given
        var originalTime = apiFactory.FakeClock.GetCurrentInstant();
        var account = await SeedAsync();
        if (!sharingEnabled) { await SetPreferenceAsync(account.User.Id, false, 1); }
        var originalIdle = Now + Duration.FromMinutes(15);
        var originalAbsolute = Now + Duration.FromHours(8);
        var originalOffline = Now + Duration.FromHours(1);
        var request = await ActivityAsync(account, Now + Duration.FromMinutes(20));
        var client = apiFactory.CreateAuthenticatedClient(account.User);

        // When
        HttpResponseMessage response;
        AuthorizeSharedUnlockResponse result;
        try
        {
            apiFactory.FakeClock.Advance(Duration.FromMinutes(5));
            (response, result) = await client
                .POSTAsync<RecordSharedUnlockActivityEndpoint, RecordSharedUnlockActivityRequest, AuthorizeSharedUnlockResponse>(request);
        }
        finally { apiFactory.FakeClock.Reset(originalTime); }

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.AuthorizationId.ShouldBe(request.AuthorizationId);
        result.Sequence.ShouldBe(1u);
        result.UnlockedAtMs.ShouldBe(Now.ToUnixTimeMilliseconds());
        result.IdleDeadlineMs.ShouldBe(request.IdleDeadlineMs);
        result.AbsoluteDeadlineMs.ShouldBe(originalAbsolute.ToUnixTimeMilliseconds());
        result.OfflineDeadlineMs.ShouldBe(originalOffline.ToUnixTimeMilliseconds());
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        var peer = await db.SharedUnlockAuthorizations.SingleAsync(a => a.UserId == account.User.Id
            && a.LinkId == account.LinkId && a.Id != request.AuthorizationId, Ct);
        peer.IdleDeadline.ShouldBe(originalIdle);
        (await db.Users.SingleAsync(u => u.Id == account.User.Id, Ct)).SharedUnlockEnabled.ShouldBe(sharingEnabled);
    }

    [Fact]
    public async Task When_ActivityRequestIsReplayedLater_Then_DeadlineDoesNotMoveAgain()
    {
        // Given
        var originalTime = apiFactory.FakeClock.GetCurrentInstant();
        var account = await SeedAsync();
        var request = await ActivityAsync(account, Now + Duration.FromMinutes(20));
        var client = apiFactory.CreateAuthenticatedClient(account.User);

        // When
        AuthorizeSharedUnlockResponse first;
        AuthorizeSharedUnlockResponse second;
        try
        {
            apiFactory.FakeClock.Advance(Duration.FromMinutes(5));
            var (response, result) = await client.POSTAsync<RecordSharedUnlockActivityEndpoint,
                RecordSharedUnlockActivityRequest, AuthorizeSharedUnlockResponse>(request);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            first = result;
            apiFactory.FakeClock.Advance(Duration.FromMinutes(4));
            (response, result) = await client.POSTAsync<RecordSharedUnlockActivityEndpoint,
                RecordSharedUnlockActivityRequest, AuthorizeSharedUnlockResponse>(request);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            second = result;
        }
        finally { apiFactory.FakeClock.Reset(originalTime); }

        // Then
        second.ShouldBe(first);
        second.IdleDeadlineMs.ShouldBe(request.IdleDeadlineMs);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("absolute")]
    [InlineData("generation")]
    [InlineData("lock")]
    [InlineData("logout")]
    [InlineData("revoke")]
    public async Task When_ActivityHasLostAuthority_Then_ItCannotRestoreUnlockOrExtendHardLimits(string change)
    {
        // Given
        var originalTime = apiFactory.FakeClock.GetCurrentInstant();
        var account = await SeedAsync();
        var request = await ActivityAsync(account, Now + Duration.FromMinutes(20));
        if (change == "absolute") { request = request with { IdleDeadlineMs = (Now + Duration.FromHours(9)).ToUnixTimeMilliseconds() }; }
        if (change == "generation") { request = request with { SourceGeneration = new byte[32] }; }
        if (change is "lock" or "logout" or "revoke")
        {
            await using var scope = apiFactory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
            var user = await db.Users.SingleAsync(u => u.Id == account.User.Id, Ct);
            user.TryAdvanceSharedUnlockSequence().ShouldBeTrue();
            var link = await db.SharedUnlockLinks.SingleAsync(l => l.UserId == user.Id && l.Id == account.LinkId, Ct);
            (change switch
            {
                "lock" => link.TryLock(2, user.SharedUnlockSequence, Now),
                "logout" => link.TryLogout(2, user.SharedUnlockSequence, Now),
                _ => link.TryDisconnect(2, user.SharedUnlockSequence, Now),
            }).ShouldBeTrue();
            await db.SaveChangesAsync(Ct);
        }

        var client = apiFactory.CreateAuthenticatedClient(account.User);

        // When
        HttpResponseMessage response;
        try
        {
            if (change == "expired") { apiFactory.FakeClock.Reset(Now + Duration.FromMinutes(15)); }
            (response, _) = await client
                .POSTAsync<RecordSharedUnlockActivityEndpoint, RecordSharedUnlockActivityRequest, AuthorizeSharedUnlockResponse>(request);
        }
        finally { apiFactory.FakeClock.Reset(originalTime); }

        // Then
        response.StatusCode.ShouldBe(change == "logout" ? HttpStatusCode.Unauthorized : HttpStatusCode.Conflict);
        await using var verification = apiFactory.Services.CreateAsyncScope();
        var stored = await verification.ServiceProvider.GetRequiredService<IdentityDbReadContext>().SharedUnlockAuthorizations
            .SingleAsync(a => a.UserId == account.User.Id && a.Id == request.AuthorizationId, Ct);
        stored.IdleDeadline.ShouldBe(Now + Duration.FromMinutes(15));
    }

    [Fact]
    public async Task When_ConcurrentActivityWritesOverlap_Then_OldDeadlineCannotOverwriteTheCommittedUpdate()
    {
        // Given
        var account = await SeedAsync();
        var request = await ActivityAsync(account, Now + Duration.FromMinutes(20));
        await using var firstScope = apiFactory.Services.CreateAsyncScope();
        await using var secondScope = apiFactory.Services.CreateAsyncScope();
        var first = firstScope.ServiceProvider.GetRequiredService<IdentityDomainWriteContext>();
        var second = secondScope.ServiceProvider.GetRequiredService<IdentityDomainWriteContext>();
        var root = await first.SharedUnlockAuthorizations.SingleAsync(a => a.UserId == account.User.Id && a.Id == request.AuthorizationId, Ct);
        var stale = await second.SharedUnlockAuthorizations.SingleAsync(a => a.UserId == account.User.Id && a.Id == request.AuthorizationId, Ct);
        root.TryRecordActivity(Now + Duration.FromMinutes(25), Now).ShouldBeTrue();
        stale.TryRecordActivity(Now + Duration.FromMinutes(20), Now).ShouldBeTrue();

        // When
        await first.CommitAsync(Ct);

        // Then
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => second.CommitAsync(Ct));
        await using var verification = apiFactory.Services.CreateAsyncScope();
        (await verification.ServiceProvider.GetRequiredService<IdentityDbReadContext>().SharedUnlockAuthorizations
            .SingleAsync(a => a.UserId == account.User.Id && a.Id == request.AuthorizationId, Ct))
            .IdleDeadline.ShouldBe(Now + Duration.FromMinutes(25));
    }

    private async Task<RecordSharedUnlockActivityRequest> ActivityAsync(Account account, Instant deadline)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        var hash = TokenService.HashToken(account.SourceRaw);
        var sessionId = await db.RefreshTokens.Where(t => t.UserId == account.User.Id && t.TokenHash == hash)
            .Select(t => t.SessionId).SingleAsync(Ct);
        var id = await db.SharedUnlockAuthorizations.Where(a => a.UserId == account.User.Id && a.SessionId == sessionId)
            .Select(a => a.Id).SingleAsync(Ct);
        return new RecordSharedUnlockActivityRequest
        {
            RefreshToken = account.SourceRaw, AuthorizationId = id, SourceGeneration = Generation,
            IdleDeadlineMs = deadline.ToUnixTimeMilliseconds(),
        };
    }

    [Fact]
    public async Task When_FreshLoginAuthorizesAfterSharedLogout_Then_LinkCanBeUsedAgainWithoutRevivingOldSessions()
    {
        // Given
        var account = await SeedAsync();
        var client = apiFactory.CreateAuthenticatedClient(account.User);
        var (loggedOut, link) = await client.POSTAsync<LogoutSharedUnlockLinkEndpoint,
            LogoutSharedUnlockLinkRequest, SharedUnlockLinkResponse>(Request(account));
        loggedOut.StatusCode.ShouldBe(HttpStatusCode.OK);
        var freshRaw = Guid.NewGuid().ToString("N");
        await apiFactory.Services.SeedRefreshTokenAsync(account.User.Id, freshRaw);
        var freshGeneration = Enumerable.Range(80, 32).Select(i => (byte)i).ToArray();

        // When
        var (authorized, root) = await client.POSTAsync<AuthorizeSharedUnlockEndpoint,
            AuthorizeSharedUnlockRequest, AuthorizeSharedUnlockResponse>(new()
        {
            RefreshToken = freshRaw, AuthCredential = Credential, SourceGeneration = freshGeneration,
            ExpectedPreferenceRevision = 1, ExpectedCredentialRevision = 1, ExpectedPrivateKeyWrapRevision = 1,
            IdleDeadlineMs = (Now + Duration.FromMinutes(15)).ToUnixTimeMilliseconds(),
            AbsoluteDeadlineMs = (Now + Duration.FromHours(8)).ToUnixTimeMilliseconds(),
            OfflineDeadlineMs = (Now + Duration.FromHours(1)).ToUnixTimeMilliseconds(),
        });
        var (activated, active) = await client.POSTAsync<ActivateSharedUnlockLinkEndpoint,
            ActivateSharedUnlockLinkRequest, SharedUnlockLinkResponse>(new()
        {
            LinkId = link.LinkId, ExpectedRevision = link.Revision, ExpectedPreferenceRevision = 1,
            RefreshToken = freshRaw, AuthorizationId = root.AuthorizationId, SourceGeneration = freshGeneration,
        });

        // Then
        authorized.StatusCode.ShouldBe(HttpStatusCode.OK);
        activated.StatusCode.ShouldBe(HttpStatusCode.OK);
        active.LastLogoutSequence.ShouldBe(link.LastLogoutSequence);
        root.Sequence.ShouldBeGreaterThan(active.LastLogoutSequence);
        (await RefreshAsync(freshRaw)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RefreshAsync(account.SourceRaw)).Response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await RefreshAsync(account.PeerRaw)).Response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private Instant Now => Instant.FromUnixTimeMilliseconds(apiFactory.FakeClock.GetCurrentInstant().ToUnixTimeMilliseconds());
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static byte[] Credential => Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static byte[] Generation => Enumerable.Range(40, 32).Select(i => (byte)i).ToArray();
    private sealed record Account(User User, Guid LinkId, Guid OtherLinkId, string SourceRaw, string PeerRaw, string OtherRaw);

    private async Task<Account> SeedAsync()
    {
        apiFactory.GuidProvider.Generate().Returns(_ => Guid.NewGuid());
        var (user, _) = await apiFactory.Services.SeedPasswordUserAsync(Credential, emailVerified: true);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
        var tracked = await db.Users.SingleAsync(u => u.Id == user.Id, Ct);
        tracked.TryAdvanceSharedUnlockSequence().ShouldBeTrue();
        var link = SharedUnlockLink.Create(user.Id, Guid.NewGuid(), Now);
        link.TryActivateFromManualUnlock(1, tracked.SharedUnlockSequence, Now).ShouldBeTrue();
        var otherLink = SharedUnlockLink.Create(user.Id, Guid.NewGuid(), Now);
        otherLink.TryActivateFromManualUnlock(1, tracked.SharedUnlockSequence, Now).ShouldBeTrue();
        db.SharedUnlockLinks.AddRange(link, otherLink);
        var raws = new[] { Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N") };
        for (var i = 0; i < raws.Length; i++)
        {
            if (i == 2) { tracked.TryAdvanceSharedUnlockSequence().ShouldBeTrue(); }
            var token = RefreshToken.Create(Guid.NewGuid(), user.Id, user.OrganizationId, TokenService.HashToken(raws[i]),
                1, Now + Duration.FromDays(1), Now);
            var root = SharedUnlockAuthorization.Create(user.Id, token.SessionId!.Value);
            root.AuthorizeManualUnlock(Guid.NewGuid(), tracked, token, Generation, Now + Duration.FromMinutes(15),
                Now + Duration.FromHours(8), Now + Duration.FromHours(1), Now);
            root.TryBind(i < 2 ? link : otherLink).ShouldBeTrue();
            db.RefreshTokens.Add(token);
            db.SharedUnlockAuthorizations.Add(root);
        }
        await db.SaveChangesAsync(Ct);
        return new Account(user, link.Id, otherLink.Id, raws[0], raws[1], raws[2]);
    }

    private async Task SetPreferenceAsync(Guid userId, bool enabled, uint revision)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
        (await db.Users.SingleAsync(u => u.Id == userId, Ct)).TrySetSharedUnlockPreference(enabled, revision, Now).ShouldBeTrue();
        await db.SaveChangesAsync(Ct);
    }

    private static LogoutSharedUnlockLinkRequest Request(Account account) => new()
    { LinkId = account.LinkId, ExpectedRevision = 2, ExpectedPreferenceRevision = 1 };

    private async Task<(HttpResponseMessage Response, RefreshAccessTokenResponse Result)> RefreshAsync(string raw)
    {
        var (response, result) = await apiFactory.CreateClient().POSTAsync<RefreshAccessTokenEndpoint,
            RefreshAccessTokenRequest, RefreshAccessTokenResponse>(new() { RefreshToken = raw });
        return (response, result);
    }
}
