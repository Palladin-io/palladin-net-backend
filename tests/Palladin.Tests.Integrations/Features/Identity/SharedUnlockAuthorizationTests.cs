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
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Identity;

[Collection<ApiFactoryCollection>]
public sealed class SharedUnlockAuthorizationTests(ApiFactory apiFactory) : TestBase
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(86400000, false)]
    [InlineData(1, true)]
    public async Task When_ClientCeilingsExceedOwnRefreshExpiry_Then_UsesAuthoritativeShorterLimits(
        long excessMilliseconds, bool idleAlsoExceeds)
    {
        // Given
        var source = await SeedSourceAsync();
        var expiry = (Now + Duration.FromDays(1)).ToUnixTimeMilliseconds();
        var request = Request(source) with
        {
            AbsoluteDeadlineMs = expiry + excessMilliseconds,
            OfflineDeadlineMs = expiry + excessMilliseconds,
            IdleDeadlineMs = idleAlsoExceeds ? expiry + excessMilliseconds : Request(source).IdleDeadlineMs,
        };

        // When
        var (response, authority) = await apiFactory.CreateAuthenticatedClient(source.User)
            .POSTAsync<AuthorizeSharedUnlockEndpoint, AuthorizeSharedUnlockRequest, AuthorizeSharedUnlockResponse>(request);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        authority.AbsoluteDeadlineMs.ShouldBe(expiry);
        authority.OfflineDeadlineMs.ShouldBe(expiry);
        authority.IdleDeadlineMs.ShouldBe(Math.Min(request.IdleDeadlineMs, expiry));
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        var stored = await db.SharedUnlockAuthorizations.SingleAsync(root => root.UserId == source.User.Id, Ct);
        stored.AbsoluteDeadline.ToUnixTimeMilliseconds().ShouldBe(authority.AbsoluteDeadlineMs);
        stored.OfflineDeadline.ToUnixTimeMilliseconds().ShouldBe(authority.OfflineDeadlineMs);
        stored.IdleDeadline.ToUnixTimeMilliseconds().ShouldBe(authority.IdleDeadlineMs);
        var session = await db.RefreshTokens.SingleAsync(token => token.UserId == source.User.Id, Ct);
        session.ExpiresAt.ToUnixTimeMilliseconds().ShouldBe(expiry);
        session.RevokedAt.ShouldBeNull();
    }

    [Fact]
    public async Task When_ManualUnlockPrecedesPeerDiscovery_Then_LaterBindingKeepsOriginalDeadlines()
    {
        // Given
        var source = await SeedSourceAsync();
        var client = apiFactory.CreateAuthenticatedClient(source.User);
        var request = Request(source);

        // When
        var (authorized, authority) = await client.POSTAsync<AuthorizeSharedUnlockEndpoint,
            AuthorizeSharedUnlockRequest, AuthorizeSharedUnlockResponse>(request);
        var link = await SeedLinkAsync(source.User.Id);
        var (activated, result) = await client.POSTAsync<ActivateSharedUnlockLinkEndpoint,
            ActivateSharedUnlockLinkRequest, SharedUnlockLinkResponse>(Activate(source, authority, link.Id, 1));

        // Then
        authorized.StatusCode.ShouldBe(HttpStatusCode.OK);
        authorized.Headers.CacheControl!.NoStore.ShouldBeTrue();
        authority.Sequence.ShouldBe(1u);
        authority.AccountId.ShouldBe(source.User.Id);
        authority.OrganizationId.ShouldBe(source.User.OrganizationId);
        activated.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.State.ShouldBe("active");
        result.Epoch.ShouldBe(2u);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        var stored = await db.SharedUnlockAuthorizations.SingleAsync(a => a.UserId == source.User.Id, Ct);
        stored.LinkId.ShouldBe(link.Id);
        stored.LinkEpoch.ShouldBe(2u);
        stored.IdleDeadline.ToUnixTimeMilliseconds().ShouldBe(request.IdleDeadlineMs);
        stored.AbsoluteDeadline.ToUnixTimeMilliseconds().ShouldBe(request.AbsoluteDeadlineMs);
        stored.OfflineDeadline.ToUnixTimeMilliseconds().ShouldBe(request.OfflineDeadlineMs);
        (await db.RefreshTokens.CountAsync(t => t.UserId == source.User.Id, Ct)).ShouldBe(1);
    }

    [Theory]
    [InlineData("password", HttpStatusCode.Unauthorized)]
    [InlineData("refresh", HttpStatusCode.Unauthorized)]
    [InlineData("revoked", HttpStatusCode.Unauthorized)]
    [InlineData("expired", HttpStatusCode.Unauthorized)]
    [InlineData("credential-revision", HttpStatusCode.Conflict)]
    [InlineData("wrap-revision", HttpStatusCode.Conflict)]
    [InlineData("deadline", HttpStatusCode.Conflict)]
    [InlineData("absolute-expired", HttpStatusCode.Conflict)]
    [InlineData("offline-expired", HttpStatusCode.Conflict)]
    [InlineData("idle-after-absolute", HttpStatusCode.Conflict)]
    [InlineData("totp", HttpStatusCode.Forbidden)]
    public async Task When_SourceAuthorityIsInvalid_Then_NoAuthorizationIsStored(string kind, HttpStatusCode expected)
    {
        // Given
        var source = await SeedSourceAsync();
        var request = Request(source);
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
            var user = await db.Users.SingleAsync(u => u.Id == source.User.Id, Ct);
            var token = await db.RefreshTokens.SingleAsync(t => t.UserId == user.Id, Ct);
            switch (kind)
            {
                case "password": request = request with { AuthCredential = new byte[32] }; break;
                case "refresh": request = request with { RefreshToken = "unrelated-session" }; break;
                case "revoked": token.Revoke(Now); break;
                case "expired":
                    db.Entry(token).Property(t => t.ExpiresAt).CurrentValue = Now;
                    break;
                case "credential-revision": request = request with { ExpectedCredentialRevision = 2 }; break;
                case "wrap-revision": request = request with { ExpectedPrivateKeyWrapRevision = 2 }; break;
                case "deadline": request = request with { IdleDeadlineMs = Now.ToUnixTimeMilliseconds() }; break;
                case "absolute-expired": request = request with { AbsoluteDeadlineMs = Now.ToUnixTimeMilliseconds() }; break;
                case "offline-expired": request = request with { OfflineDeadlineMs = Now.ToUnixTimeMilliseconds() }; break;
                case "idle-after-absolute": request = request with { IdleDeadlineMs = request.AbsoluteDeadlineMs + 1 }; break;
                case "totp":
                    var factor = TotpCredential.StartEnrollment(user.Id, "synthetic-factor", Now);
                    factor.Confirm(0, Now);
                    db.TotpCredentials.Add(factor);
                    break;
            }
            await db.SaveChangesAsync(Ct);
        }

        // When
        var (response, _) = await apiFactory.CreateAuthenticatedClient(source.User)
            .POSTAsync<AuthorizeSharedUnlockEndpoint, AuthorizeSharedUnlockRequest, AuthorizeSharedUnlockResponse>(request);

        // Then
        response.StatusCode.ShouldBe(expected);
        await using var verification = apiFactory.Services.CreateAsyncScope();
        var read = verification.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        (await read.SharedUnlockAuthorizations.AnyAsync(a => a.UserId == source.User.Id, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task When_LockAndNewUnlockHaveIdenticalTimes_Then_OnlyFreshPasswordAuthorizationCanReactivate()
    {
        // Given
        var source = await SeedSourceAsync();
        var client = apiFactory.CreateAuthenticatedClient(source.User);
        var link = await SeedLinkAsync(source.User.Id);

        // When
        var (created, first) = await client.POSTAsync<AuthorizeSharedUnlockEndpoint,
            AuthorizeSharedUnlockRequest, AuthorizeSharedUnlockResponse>(Request(source));
        created.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (activated, _) = await client.POSTAsync<ActivateSharedUnlockLinkEndpoint,
            ActivateSharedUnlockLinkRequest, SharedUnlockLinkResponse>(Activate(source, first, link.Id, 1));
        activated.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (locked, lockedLink) = await client.POSTAsync<LockSharedUnlockLinkEndpoint,
            LockSharedUnlockLinkRequest, SharedUnlockLinkResponse>(new()
            { LinkId = link.Id, ExpectedRevision = 2, ExpectedPreferenceRevision = 1 });
        var (stale, _) = await client.POSTAsync<ActivateSharedUnlockLinkEndpoint,
            ActivateSharedUnlockLinkRequest, SharedUnlockLinkResponse>(Activate(source, first, link.Id, lockedLink.Revision));
        var (refreshed, second) = await client.POSTAsync<AuthorizeSharedUnlockEndpoint,
            AuthorizeSharedUnlockRequest, AuthorizeSharedUnlockResponse>(Request(source));
        var (restored, _) = await client.POSTAsync<ActivateSharedUnlockLinkEndpoint,
            ActivateSharedUnlockLinkRequest, SharedUnlockLinkResponse>(Activate(source, second, link.Id, lockedLink.Revision));

        // Then
        locked.StatusCode.ShouldBe(HttpStatusCode.OK);
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        refreshed.StatusCode.ShouldBe(HttpStatusCode.OK);
        restored.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.AuthorizationId.ShouldNotBe(first.AuthorizationId);
        second.Sequence.ShouldBe(3u);
        second.UnlockedAtMs.ShouldBe(first.UnlockedAtMs);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>()
            .SharedUnlockAuthorizations.CountAsync(a => a.UserId == source.User.Id, Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task When_OldUnboundAuthorizationSurvivesDisconnectReconnect_Then_ItCannotBind()
    {
        // Given
        var source = await SeedSourceAsync();
        var client = apiFactory.CreateAuthenticatedClient(source.User);
        var link = await SeedLinkAsync(source.User.Id);

        // When
        var (created, authority) = await client.POSTAsync<AuthorizeSharedUnlockEndpoint,
            AuthorizeSharedUnlockRequest, AuthorizeSharedUnlockResponse>(Request(source));
        created.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (disconnected, _) = await client.POSTAsync<DisconnectSharedUnlockLinkEndpoint,
            DisconnectSharedUnlockLinkRequest, SharedUnlockLinkResponse>(new() { LinkId = link.Id, ExpectedRevision = 1 });
        var (reconnected, result) = await client.POSTAsync<ReconnectSharedUnlockLinkEndpoint,
            ReconnectSharedUnlockLinkRequest, SharedUnlockLinkResponse>(new() { LinkId = link.Id, ExpectedRevision = 2 });
        var (stale, _) = await client.POSTAsync<ActivateSharedUnlockLinkEndpoint,
            ActivateSharedUnlockLinkRequest, SharedUnlockLinkResponse>(Activate(source, authority, link.Id, result.Revision));

        // Then
        disconnected.StatusCode.ShouldBe(HttpStatusCode.OK);
        reconnected.StatusCode.ShouldBe(HttpStatusCode.OK);
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        result.State.ShouldBe("locked");
    }

    [Fact]
    public async Task When_SourceRefreshRotates_Then_AuthoritySurvivesWithoutNewDeadlines()
    {
        // Given
        var source = await SeedSourceAsync();
        var client = apiFactory.CreateAuthenticatedClient(source.User);
        var link = await SeedLinkAsync(source.User.Id);

        // When
        var (created, authority) = await client.POSTAsync<AuthorizeSharedUnlockEndpoint,
            AuthorizeSharedUnlockRequest, AuthorizeSharedUnlockResponse>(Request(source));
        created.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (refreshed, tokens) = await apiFactory.CreateClient().POSTAsync<RefreshAccessTokenEndpoint,
            RefreshAccessTokenRequest, RefreshAccessTokenResponse>(new() { RefreshToken = source.RawToken });
        var (activated, _) = await client.POSTAsync<ActivateSharedUnlockLinkEndpoint,
            ActivateSharedUnlockLinkRequest, SharedUnlockLinkResponse>(Activate(source, authority, link.Id, 1)
                with { RefreshToken = tokens.RefreshToken });

        // Then
        refreshed.StatusCode.ShouldBe(HttpStatusCode.OK);
        tokens.RefreshToken.ShouldNotBe(source.RawToken);
        activated.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        var stored = await db.SharedUnlockAuthorizations.SingleAsync(a => a.UserId == source.User.Id, Ct);
        stored.AbsoluteDeadline.ToUnixTimeMilliseconds().ShouldBe(authority.AbsoluteDeadlineMs);
        stored.Id.ShouldBe(authority.AuthorizationId);
        var hash = TokenService.HashToken(tokens.RefreshToken);
        var rotated = await db.RefreshTokens.SingleAsync(t => t.UserId == source.User.Id && t.TokenHash == hash, Ct);
        rotated.SessionId.ShouldBe(stored.SessionId);
    }

    [Fact]
    public async Task When_GenerationOrLinkIsSubstituted_Then_BindingIsRejected()
    {
        // Given
        var source = await SeedSourceAsync();
        var client = apiFactory.CreateAuthenticatedClient(source.User);
        var link = await SeedLinkAsync(source.User.Id);
        var other = await SeedLinkAsync(source.User.Id);

        // When
        var (created, authority) = await client.POSTAsync<AuthorizeSharedUnlockEndpoint,
            AuthorizeSharedUnlockRequest, AuthorizeSharedUnlockResponse>(Request(source));
        created.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (wrongGeneration, _) = await client.POSTAsync<ActivateSharedUnlockLinkEndpoint,
            ActivateSharedUnlockLinkRequest, SharedUnlockLinkResponse>(Activate(source, authority, link.Id, 1)
                with { SourceGeneration = new byte[32] });
        var (valid, _) = await client.POSTAsync<ActivateSharedUnlockLinkEndpoint,
            ActivateSharedUnlockLinkRequest, SharedUnlockLinkResponse>(Activate(source, authority, link.Id, 1));
        var (otherLink, _) = await client.POSTAsync<ActivateSharedUnlockLinkEndpoint,
            ActivateSharedUnlockLinkRequest, SharedUnlockLinkResponse>(Activate(source, authority, other.Id, 1));

        // Then
        wrongGeneration.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        valid.StatusCode.ShouldBe(HttpStatusCode.OK);
        otherLink.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task When_ForeignOrAnonymousClientSuppliesSourceToken_Then_NoAuthorityIsCreated()
    {
        // Given
        var source = await SeedSourceAsync();
        var other = await SeedSourceAsync();

        // When
        var (foreign, _) = await apiFactory.CreateAuthenticatedClient(other.User)
            .POSTAsync<AuthorizeSharedUnlockEndpoint, AuthorizeSharedUnlockRequest, AuthorizeSharedUnlockResponse>(Request(source));
        var (anonymous, _) = await apiFactory.CreateClient()
            .POSTAsync<AuthorizeSharedUnlockEndpoint, AuthorizeSharedUnlockRequest, AuthorizeSharedUnlockResponse>(Request(source));

        // Then
        foreign.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("off", HttpStatusCode.Conflict)]
    [InlineData("credential", HttpStatusCode.Conflict)]
    [InlineData("wrap", HttpStatusCode.Conflict)]
    [InlineData("expired-lease", HttpStatusCode.Conflict)]
    [InlineData("revoked-session", HttpStatusCode.Unauthorized)]
    [InlineData("new-factor", HttpStatusCode.Conflict)]
    [InlineData("removing-member", HttpStatusCode.Forbidden)]
    public async Task When_AuthorityChangesBeforeBinding_Then_OldAuthorizationCannotActivate(
        string change, HttpStatusCode expected)
    {
        // Given
        var source = await SeedSourceAsync();
        var client = apiFactory.CreateAuthenticatedClient(source.User);
        var link = await SeedLinkAsync(source.User.Id);

        // When
        var (created, authority) = await client.POSTAsync<AuthorizeSharedUnlockEndpoint,
            AuthorizeSharedUnlockRequest, AuthorizeSharedUnlockResponse>(Request(source));
        created.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
            var user = await db.Users.SingleAsync(u => u.Id == source.User.Id, Ct);
            switch (change)
            {
                case "off": user.TrySetSharedUnlockPreference(false, 1, Now).ShouldBeTrue(); break;
                case "credential": db.Entry(user).Property(u => u.CredentialRevision).CurrentValue = 2; break;
                case "wrap": db.Entry(user).Property(u => u.PrivateKeyWrapRevision).CurrentValue = 2; break;
                case "expired-lease":
                    var stored = await db.SharedUnlockAuthorizations.SingleAsync(a => a.UserId == user.Id, Ct);
                    db.Entry(stored).Property(a => a.IdleDeadline).CurrentValue = Now;
                    break;
                case "revoked-session":
                    (await db.RefreshTokens.SingleAsync(t => t.UserId == user.Id, Ct)).Revoke(Now);
                    break;
                case "new-factor":
                    var factor = TotpCredential.StartEnrollment(user.Id, "synthetic-factor", Now);
                    factor.Confirm(0, Now);
                    db.TotpCredentials.Add(factor);
                    user.TryAdvanceSharedUnlockSequence().ShouldBeTrue();
                    break;
                case "removing-member":
                    var membership = await db.OrganizationMembers.SingleAsync(m => m.UserId == user.Id, Ct);
                    db.Entry(membership).Property(m => m.Status).CurrentValue = OrganizationMemberStatus.Removing;
                    break;
            }
            await db.SaveChangesAsync(Ct);
        }
        var (response, _) = await client.POSTAsync<ActivateSharedUnlockLinkEndpoint,
            ActivateSharedUnlockLinkRequest, SharedUnlockLinkResponse>(Activate(source, authority, link.Id, 1));

        // Then
        response.StatusCode.ShouldBe(expected);
        await using var verification = apiFactory.Services.CreateAsyncScope();
        var read = verification.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        (await read.SharedUnlockLinks.SingleAsync(l => l.UserId == source.User.Id && l.Id == link.Id, Ct))
            .State.ShouldBe(SharedUnlockLinkState.Locked);
    }

    [Fact]
    public async Task When_TwoLinksRaceForOneAuthorization_Then_OnlyOneBindingCommits()
    {
        // Given
        var source = await SeedSourceAsync();
        var firstLink = await SeedLinkAsync(source.User.Id);
        var secondLink = await SeedLinkAsync(source.User.Id);
        var (created, _) = await apiFactory.CreateAuthenticatedClient(source.User)
            .POSTAsync<AuthorizeSharedUnlockEndpoint, AuthorizeSharedUnlockRequest, AuthorizeSharedUnlockResponse>(Request(source));
        created.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var firstScope = apiFactory.Services.CreateAsyncScope();
        await using var secondScope = apiFactory.Services.CreateAsyncScope();
        var first = firstScope.ServiceProvider.GetRequiredService<IdentityDomainWriteContext>();
        var second = secondScope.ServiceProvider.GetRequiredService<IdentityDomainWriteContext>();
        foreach (var (context, linkId) in new[] { (first, firstLink.Id), (second, secondLink.Id) })
        {
            var authority = await context.SharedUnlockAuthorizations.SingleAsync(a => a.UserId == source.User.Id, Ct);
            var link = await context.SharedUnlockLinks.SingleAsync(l => l.UserId == source.User.Id && l.Id == linkId, Ct);
            link.TryActivateFromManualUnlock(1, authority.Sequence, Now).ShouldBeTrue();
            authority.TryBind(link).ShouldBeTrue();
            context.MarkPropertyAsUpdated(authority, a => a.Sequence);
            context.MarkPropertyAsUpdated(link, l => l.Revision);
        }

        // When
        await first.CommitAsync(Ct);

        // Then
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => second.CommitAsync(Ct));
        await using var verification = apiFactory.Services.CreateAsyncScope();
        var db = verification.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        (await db.SharedUnlockAuthorizations.SingleAsync(a => a.UserId == source.User.Id, Ct)).LinkId.ShouldBe(firstLink.Id);
        (await db.SharedUnlockLinks.SingleAsync(l => l.UserId == source.User.Id && l.Id == firstLink.Id, Ct)).State.ShouldBe(SharedUnlockLinkState.Active);
        (await db.SharedUnlockLinks.SingleAsync(l => l.UserId == source.User.Id && l.Id == secondLink.Id, Ct)).State.ShouldBe(SharedUnlockLinkState.Locked);
    }

    [Theory]
    [InlineData("logout")]
    [InlineData("membership")]
    [InlineData("factor-creation")]
    public async Task When_SourceFenceChangesDuringAuthorization_Then_EntireNewAuthorizationRollsBack(string change)
    {
        // Given
        var source = await SeedSourceAsync();
        await using var attemptScope = apiFactory.Services.CreateAsyncScope();
        var attempt = attemptScope.ServiceProvider.GetRequiredService<IdentityDomainWriteContext>();
        var user = await attempt.Users.SingleAsync(u => u.Id == source.User.Id, Ct);
        var session = await attempt.RefreshTokens.SingleAsync(t => t.UserId == user.Id, Ct);
        var member = await attempt.OrganizationMembers.SingleAsync(m => m.UserId == user.Id, Ct);
        user.TryAdvanceSharedUnlockSequence().ShouldBeTrue();
        var authority = SharedUnlockAuthorization.Create(user.Id, session.SessionId ?? session.Id);
        authority.AuthorizeManualUnlock(Guid.NewGuid(), user, session, Generation,
            Now + Duration.FromMinutes(15), Now + Duration.FromHours(8), Now + Duration.FromHours(1), Now);
        attempt.Add(authority);
        attempt.MarkPropertyAsUpdated(session, t => t.RevokedAt);
        attempt.MarkPropertyAsUpdated(member, m => m.Status);

        // When
        await using (var mutationScope = apiFactory.Services.CreateAsyncScope())
        {
            var mutation = mutationScope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
            if (change == "logout")
            {
                (await mutation.RefreshTokens.SingleAsync(t => t.UserId == user.Id, Ct)).Revoke(Now);
            }
            else if (change == "membership")
            {
                var current = await mutation.OrganizationMembers.SingleAsync(m => m.UserId == user.Id, Ct);
                mutation.Entry(current).Property(m => m.Status).CurrentValue = OrganizationMemberStatus.Removing;
            }
            else
            {
                var current = await mutation.Users.SingleAsync(u => u.Id == user.Id, Ct);
                current.TryAdvanceSharedUnlockSequence().ShouldBeTrue();
                var factor = TotpCredential.StartEnrollment(user.Id, "synthetic-factor", Now);
                factor.Confirm(0, Now);
                mutation.TotpCredentials.Add(factor);
            }
            await mutation.SaveChangesAsync(Ct);
        }

        // Then
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => attempt.CommitAsync(Ct));
        await using var verification = apiFactory.Services.CreateAsyncScope();
        var read = verification.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        (await read.SharedUnlockAuthorizations.AnyAsync(a => a.UserId == user.Id, Ct)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task When_SourceAlreadyCompletedCurrentTotp_Then_ManualUnlockKeepsThatVerificationAge(bool justVerified)
    {
        // Given
        var source = await SeedSourceAsync();
        var verifiedAt = justVerified ? apiFactory.FakeClock.GetCurrentInstant() : Now - Duration.FromMinutes(5);
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
            var factor = TotpCredential.StartEnrollment(source.User.Id, "synthetic-factor", verifiedAt);
            factor.Confirm(0, verifiedAt);
            db.TotpCredentials.Add(factor);
            var session = await db.RefreshTokens.SingleAsync(t => t.UserId == source.User.Id, Ct);
            db.Entry(session).Property(t => t.SecondFactorRevision).CurrentValue = factor.ConfigurationRevision;
            db.Entry(session).Property(t => t.SecondFactorVerifiedAt).CurrentValue = verifiedAt;
            await db.SaveChangesAsync(Ct);
        }

        // When
        var (response, _) = await apiFactory.CreateAuthenticatedClient(source.User)
            .POSTAsync<AuthorizeSharedUnlockEndpoint, AuthorizeSharedUnlockRequest, AuthorizeSharedUnlockResponse>(Request(source));

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var verification = apiFactory.Services.CreateAsyncScope();
        var read = verification.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        var authority = await read.SharedUnlockAuthorizations.SingleAsync(a => a.UserId == source.User.Id, Ct);
        authority.SecondFactorRevision.ShouldBe(2u);
        var persistedSession = await read.RefreshTokens.SingleAsync(t => t.UserId == source.User.Id, Ct);
        authority.SecondFactorVerifiedAt.ShouldBe(persistedSession.SecondFactorVerifiedAt);
    }

    [Fact]
    public async Task When_ManualUnlockOccursWhileOff_Then_OwnAuthorityCanBeUsedOnlyAfterSharingIsEnabled()
    {
        // Given
        var source = await SeedSourceAsync();
        var link = await SeedLinkAsync(source.User.Id);
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
            (await db.Users.SingleAsync(u => u.Id == source.User.Id, Ct)).TrySetSharedUnlockPreference(false, 1, Now).ShouldBeTrue();
            await db.SaveChangesAsync(Ct);
        }
        var client = apiFactory.CreateAuthenticatedClient(source.User);

        // When
        var (created, root) = await client.POSTAsync<AuthorizeSharedUnlockEndpoint,
            AuthorizeSharedUnlockRequest, AuthorizeSharedUnlockResponse>(Request(source) with { ExpectedPreferenceRevision = 2 });
        var (disabled, _) = await client.POSTAsync<ActivateSharedUnlockLinkEndpoint,
            ActivateSharedUnlockLinkRequest, SharedUnlockLinkResponse>(Activate(source, root, link.Id, 1) with { ExpectedPreferenceRevision = 2 });
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
            (await db.Users.SingleAsync(u => u.Id == source.User.Id, Ct)).TrySetSharedUnlockPreference(true, 2, Now).ShouldBeTrue();
            await db.SaveChangesAsync(Ct);
        }
        var (enabled, _) = await client.POSTAsync<ActivateSharedUnlockLinkEndpoint,
            ActivateSharedUnlockLinkRequest, SharedUnlockLinkResponse>(Activate(source, root, link.Id, 1) with { ExpectedPreferenceRevision = 3 });

        // Then
        created.StatusCode.ShouldBe(HttpStatusCode.OK);
        disabled.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        enabled.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var verification = apiFactory.Services.CreateAsyncScope();
        var read = verification.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        (await read.RefreshTokens.CountAsync(t => t.UserId == source.User.Id, Ct)).ShouldBe(1);
        (await read.SharedUnlockAuthorizations.SingleAsync(a => a.UserId == source.User.Id, Ct))
            .AbsoluteDeadline.ToUnixTimeMilliseconds().ShouldBe(root.AbsoluteDeadlineMs);
    }

    private Instant Now => Instant.FromUnixTimeMilliseconds(apiFactory.FakeClock.GetCurrentInstant().ToUnixTimeMilliseconds());
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static byte[] Generation => Enumerable.Range(70, 32).Select(value => (byte)value).ToArray();
    private sealed record Source(User User, string RawToken, byte[] Credential);

    private async Task<Source> SeedSourceAsync()
    {
        apiFactory.GuidProvider.Generate().Returns(_ => Guid.NewGuid());
        var credential = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        var (user, _) = await apiFactory.Services.SeedPasswordUserAsync(credential, emailVerified: true);
        var raw = Guid.NewGuid().ToString("N");
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
        db.RefreshTokens.Add(RefreshToken.Create(Guid.NewGuid(), user.Id, user.OrganizationId,
            TokenService.HashToken(raw), 1, Now + Duration.FromDays(1), Now));
        await db.SaveChangesAsync(Ct);
        return new Source(user, raw, credential);
    }

    private async Task<SharedUnlockLink> SeedLinkAsync(Guid userId)
    {
        var link = SharedUnlockLink.Create(userId, Guid.NewGuid(), Now);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
        db.SharedUnlockLinks.Add(link);
        await db.SaveChangesAsync(Ct);
        return link;
    }

    private AuthorizeSharedUnlockRequest Request(Source source) => new()
    {
        RefreshToken = source.RawToken, AuthCredential = source.Credential, SourceGeneration = Generation,
        ExpectedPreferenceRevision = 1, ExpectedCredentialRevision = 1, ExpectedPrivateKeyWrapRevision = 1,
        IdleDeadlineMs = (Now + Duration.FromMinutes(15)).ToUnixTimeMilliseconds(),
        AbsoluteDeadlineMs = (Now + Duration.FromHours(8)).ToUnixTimeMilliseconds(),
        OfflineDeadlineMs = (Now + Duration.FromHours(1)).ToUnixTimeMilliseconds(),
    };

    private static ActivateSharedUnlockLinkRequest Activate(Source source, AuthorizeSharedUnlockResponse authority,
        Guid linkId, uint revision) => new()
    {
        LinkId = linkId, AuthorizationId = authority.AuthorizationId, RefreshToken = source.RawToken,
        SourceGeneration = Generation, ExpectedRevision = revision, ExpectedPreferenceRevision = 1,
    };
}
