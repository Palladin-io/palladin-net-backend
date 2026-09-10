using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NodaTime;
using NSec.Cryptography;
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
public sealed class SharedUnlockOperationTests(ApiFactory apiFactory) : TestBase
{
    [Theory]
    [InlineData("web-to-extension")]
    [InlineData("extension-to-web")]
    public async Task When_ReceiverCommits_Then_ItGetsIndependentTokensAndOriginalUnlockLimits(string direction)
    {
        // Given
        var source = await SeedSourceAsync();
        using var receiver = Key.Create(SignatureAlgorithm.Ed25519);

        // When
        var operation = await OfferAsync(source, receiver, direction);
        var (consumed, _) = await ConsumeAsync(operation, receiver);
        var (committed, result) = await CommitAsync(operation, receiver);

        // Then
        consumed.StatusCode.ShouldBe(HttpStatusCode.OK);
        committed.StatusCode.ShouldBe(HttpStatusCode.OK);
        committed.Headers.CacheControl!.NoStore.ShouldBeTrue();
        result.Session.RefreshToken.ShouldNotBe(source.RawToken);
        result.Session.UserId.ShouldBe(source.User.Id);
        result.Context.ShouldBe(operation.Context);
        result.AuthorizationId.ShouldNotBe(source.Authorization.Id);
        result.AuthorizationSequence.ShouldBe(source.Authorization.Sequence);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        var hash = TokenService.HashToken(result.Session.RefreshToken);
        var session = await db.RefreshTokens.SingleAsync(t => t.UserId == source.User.Id && t.TokenHash == hash, Ct);
        session.SessionId.ShouldNotBe(source.Authorization.SessionId);
        session.SecondFactorRevision.ShouldBe(source.Authorization.SecondFactorRevision);
        session.SecondFactorVerifiedAt.ShouldBe(source.Authorization.SecondFactorVerifiedAt);
        var inherited = await db.SharedUnlockAuthorizations.SingleAsync(a => a.UserId == source.User.Id && a.Id == result.AuthorizationId, Ct);
        inherited.SessionId.ShouldBe(session.SessionId!.Value);
        inherited.Sequence.ShouldBe(source.Authorization.Sequence);
        inherited.UnlockedAt.ShouldBe(source.Authorization.UnlockedAt);
        inherited.IdleDeadline.ShouldBe(source.Authorization.IdleDeadline);
        inherited.AbsoluteDeadline.ShouldBe(source.Authorization.AbsoluteDeadline);
        inherited.OfflineDeadline.ShouldBe(source.Authorization.OfflineDeadline);
        inherited.SourceGeneration.ShouldBe(RecipientGeneration);
        inherited.LinkId.ShouldBe(source.Link.Id);
        inherited.LinkEpoch.ShouldBe(source.Link.Epoch);
        var stored = await db.SharedUnlockOperations.SingleAsync(o => o.Id == operation.Context.OperationId, Ct);
        stored.State.ShouldBe(SharedUnlockOperationState.Committed);
        stored.RecipientSessionId.ShouldBe(session.SessionId);
    }

    [Fact]
    public async Task When_OriginalSourceIsGone_Then_ReceiverCanUnlockNewPeerWithoutRenewingLimits()
    {
        // Given
        var source = await SeedSourceAsync();
        using var receiver = Key.Create(SignatureAlgorithm.Ed25519);
        var operation = await OfferAsync(source, receiver);
        (await ConsumeAsync(operation, receiver)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (firstResponse, first) = await CommitAsync(operation, receiver);
        firstResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
            (await db.RefreshTokens.SingleAsync(t => t.Id == source.Authorization.SessionId, Ct)).Revoke(Now);
            await db.SaveChangesAsync(Ct);
        }
        using var nextReceiver = Key.Create(SignatureAlgorithm.Ed25519);
        var nextRequest = Request(source, nextReceiver, "extension-to-web") with
        {
            RefreshToken = first.Session.RefreshToken, AuthorizationId = first.AuthorizationId,
            ExtensionGeneration = RecipientGeneration, WebGeneration = NewWebGeneration,
        };

        // When
        var extensionClient = apiFactory.CreateClient();
        extensionClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", first.Session.AccessToken);
        var (offered, next) = await extensionClient
            .POSTAsync<CreateSharedUnlockOperationEndpoint, CreateSharedUnlockOperationRequest, SharedUnlockOperationResponse>(nextRequest);
        var (consumed, _) = await ConsumeAsync(next, nextReceiver);
        var (committed, final) = await CommitAsync(next, nextReceiver);

        // Then
        offered.StatusCode.ShouldBe(HttpStatusCode.OK);
        consumed.StatusCode.ShouldBe(HttpStatusCode.OK);
        committed.StatusCode.ShouldBe(HttpStatusCode.OK);
        final.Context.UnlockedAtMs.ShouldBe(first.Context.UnlockedAtMs);
        final.Context.IdleDeadlineMs.ShouldBe(first.Context.IdleDeadlineMs);
        final.Context.AbsoluteDeadlineMs.ShouldBe(first.Context.AbsoluteDeadlineMs);
        final.Context.OfflineDeadlineMs.ShouldBe(first.Context.OfflineDeadlineMs);
        final.Session.RefreshToken.ShouldNotBe(first.Session.RefreshToken);
    }

    [Theory]
    [InlineData("wrong-key")]
    [InlineData("wrong-purpose")]
    [InlineData("wrong-operation")]
    [InlineData("wrong-transcript")]
    public async Task When_ReceiverProofIsSubstituted_Then_OperationRemainsUnconsumed(string change)
    {
        // Given
        var source = await SeedSourceAsync();
        using var receiver = Key.Create(SignatureAlgorithm.Ed25519);
        using var other = Key.Create(SignatureAlgorithm.Ed25519);
        var operation = await OfferAsync(source, receiver);
        var authority = Proof(operation);
        if (change == "wrong-operation") { authority = authority with { OperationId = Guid.NewGuid() }; }
        if (change == "wrong-transcript") { authority = authority with { TranscriptHash = new byte[32] }; }
        var signature = Sign(authority, change == "wrong-key" ? other : receiver,
            change == "wrong-purpose" ? SharedUnlockProofPurpose.Commit : SharedUnlockProofPurpose.Consume);

        // When
        var (response, _) = await apiFactory.CreateClient().POSTAsync<ConsumeSharedUnlockOperationEndpoint,
            ConsumeSharedUnlockOperationRequest, SharedUnlockOperationResponse>(new()
            { OperationId = operation.Context.OperationId, Signature = signature });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        (await db.SharedUnlockOperations.SingleAsync(o => o.Id == operation.Context.OperationId, Ct)).State.ShouldBe(SharedUnlockOperationState.Offered);
        (await db.RefreshTokens.CountAsync(t => t.UserId == source.User.Id, Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task When_ReceiverReplaysOrCommitsBeforeConsume_Then_OnlyOrderedFirstCommitIssuesSession()
    {
        // Given
        var source = await SeedSourceAsync();
        using var receiver = Key.Create(SignatureAlgorithm.Ed25519);
        var operation = await OfferAsync(source, receiver);

        // When
        var (early, _) = await CommitAsync(operation, receiver);
        var (consumed, _) = await ConsumeAsync(operation, receiver);
        var (replayedConsume, _) = await ConsumeAsync(operation, receiver);
        var (committed, _) = await CommitAsync(operation, receiver);
        var (replayedCommit, _) = await CommitAsync(operation, receiver);

        // Then
        early.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        consumed.StatusCode.ShouldBe(HttpStatusCode.OK);
        replayedConsume.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        committed.StatusCode.ShouldBe(HttpStatusCode.OK);
        replayedCommit.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        (await db.RefreshTokens.CountAsync(t => t.UserId == source.User.Id, Ct)).ShouldBe(2);
        (await db.SharedUnlockAuthorizations.CountAsync(a => a.UserId == source.User.Id, Ct)).ShouldBe(2);
    }

    [Theory]
    [InlineData("off", false)]
    [InlineData("lock", false)]
    [InlineData("credential", false)]
    [InlineData("wrap", false)]
    [InlineData("policy", false)]
    [InlineData("off", true)]
    [InlineData("lock", true)]
    [InlineData("credential", true)]
    [InlineData("wrap", true)]
    [InlineData("policy", true)]
    [InlineData("member", true)]
    [InlineData("factor", true)]
    [InlineData("source-session", true)]
    [InlineData("source-generation", true)]
    [InlineData("source-idle", true)]
    public async Task When_AuthorityChangesDuringTransfer_Then_NoReceiverSessionIsIssued(string change, bool afterConsume)
    {
        // Given
        var source = await SeedSourceAsync();
        using var receiver = Key.Create(SignatureAlgorithm.Ed25519);
        var operation = await OfferAsync(source, receiver);
        if (afterConsume)
        {
            (await ConsumeAsync(operation, receiver)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
            var user = await db.Users.SingleAsync(u => u.Id == source.User.Id, Ct);
            switch (change)
            {
                case "off": user.TrySetSharedUnlockPreference(false, 1, Now).ShouldBeTrue(); break;
                case "lock":
                    user.TryAdvanceSharedUnlockSequence().ShouldBeTrue();
                    (await db.SharedUnlockLinks.SingleAsync(l => l.UserId == user.Id, Ct))
                        .TryLock(2, user.SharedUnlockSequence, Now).ShouldBeTrue();
                    break;
                case "credential": db.Entry(user).Property(u => u.CredentialRevision).CurrentValue = 2; break;
                case "wrap": db.Entry(user).Property(u => u.PrivateKeyWrapRevision).CurrentValue = 2; break;
                case "policy":
                    var org = await db.Organizations.SingleAsync(o => o.Id == user.OrganizationId, Ct);
                    db.Entry(org).Property(o => o.OfflineAccessPolicyVersion).CurrentValue++;
                    break;
                case "member":
                    var member = await db.OrganizationMembers.SingleAsync(m => m.UserId == user.Id, Ct);
                    db.Entry(member).Property(m => m.AuthorizationVersion).CurrentValue++;
                    break;
                case "factor":
                    var factor = await db.TotpCredentials.SingleAsync(f => f.UserId == user.Id, Ct);
                    factor.Disable(Now);
                    factor.RestartEnrollment("synthetic-replacement", Now);
                    factor.Confirm(1, Now);
                    break;
                case "source-session":
                    (await db.RefreshTokens.SingleAsync(t => t.UserId == user.Id, Ct)).Revoke(Now);
                    break;
                case "source-generation":
                    var root = await db.SharedUnlockAuthorizations.SingleAsync(a => a.UserId == user.Id, Ct);
                    db.Entry(root).Property(a => a.SourceGeneration).CurrentValue = new byte[32];
                    break;
                case "source-idle":
                    var expired = await db.SharedUnlockAuthorizations.SingleAsync(a => a.UserId == user.Id, Ct);
                    db.Entry(expired).Property(a => a.IdleDeadline).CurrentValue = Now;
                    break;
            }
            await db.SaveChangesAsync(Ct);
        }

        // When
        var status = afterConsume
            ? (await CommitAsync(operation, receiver)).Response.StatusCode
            : (await ConsumeAsync(operation, receiver)).Response.StatusCode;

        // Then
        status.ShouldBe(HttpStatusCode.Conflict);
        await using var verification = apiFactory.Services.CreateAsyncScope();
        var read = verification.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        (await read.RefreshTokens.CountAsync(t => t.UserId == source.User.Id, Ct)).ShouldBe(1);
        (await read.SharedUnlockAuthorizations.CountAsync(a => a.UserId == source.User.Id, Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task When_TwoCommitsRace_Then_OnlyOneIndependentSessionIsPersisted()
    {
        // Given
        var source = await SeedSourceAsync();
        using var receiver = Key.Create(SignatureAlgorithm.Ed25519);
        var operation = await OfferAsync(source, receiver);
        (await ConsumeAsync(operation, receiver)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // When
        var results = await Task.WhenAll(CommitAsync(operation, receiver), CommitAsync(operation, receiver));

        // Then
        results.Count(result => result.Response.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        results.Count(result => result.Response.StatusCode == HttpStatusCode.Conflict).ShouldBe(1);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        (await db.RefreshTokens.CountAsync(t => t.UserId == source.User.Id, Ct)).ShouldBe(2);
        (await db.SharedUnlockAuthorizations.CountAsync(a => a.UserId == source.User.Id, Ct)).ShouldBe(2);
    }

    [Theory]
    [InlineData("foreign-account")]
    [InlineData("foreign-organization")]
    [InlineData("generation")]
    public async Task When_SourceScopeIsSubstituted_Then_NoOperationIsCreated(string change)
    {
        // Given
        var source = await SeedSourceAsync();
        var other = await SeedSourceAsync();
        using var receiver = Key.Create(SignatureAlgorithm.Ed25519);
        var request = Request(source, receiver);
        if (change == "foreign-organization") { request = request with { RecipientOrganizationId = other.User.OrganizationId }; }
        if (change == "generation") { request = request with { WebGeneration = new byte[32] }; }
        var client = apiFactory.CreateAuthenticatedClient(change == "foreign-account" ? other.User : source.User);

        // When
        var (response, _) = await client.POSTAsync<CreateSharedUnlockOperationEndpoint,
            CreateSharedUnlockOperationRequest, SharedUnlockOperationResponse>(request);

        // Then
        response.StatusCode.ShouldBe(change == "foreign-account" ? HttpStatusCode.Unauthorized : HttpStatusCode.Conflict);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>()
            .SharedUnlockOperations.AnyAsync(o => o.UserId == source.User.Id, Ct)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task When_OperationReachesExactExpiry_Then_ConsumeOrCommitCannotIssueSession(bool afterConsume)
    {
        // Given
        var originalTime = apiFactory.FakeClock.GetCurrentInstant();
        var source = await SeedSourceAsync();
        using var receiver = Key.Create(SignatureAlgorithm.Ed25519);
        var operation = await OfferAsync(source, receiver);
        if (afterConsume)
        {
            (await ConsumeAsync(operation, receiver)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // When
        HttpStatusCode status;
        try
        {
            apiFactory.FakeClock.Reset(Instant.FromUnixTimeMilliseconds(operation.Context.ExpiresAtMs));
            status = afterConsume ? (await CommitAsync(operation, receiver)).Response.StatusCode
                : (await ConsumeAsync(operation, receiver)).Response.StatusCode;
        }
        finally
        {
            apiFactory.FakeClock.Reset(originalTime);
        }

        // Then
        status.ShouldBe(HttpStatusCode.Unauthorized);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>().RefreshTokens
            .CountAsync(t => t.UserId == source.User.Id, Ct)).ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task When_ReceiverSelectsAnotherOwnOrganization_Then_IssuanceUsesItsCurrentMembership(bool removedBeforeCommit)
    {
        // Given
        var source = await SeedSourceAsync();
        var targetOwner = await SeedSourceAsync();
        using var receiver = Key.Create(SignatureAlgorithm.Ed25519);
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
            var role = await db.Roles.SingleAsync(r => r.OrganizationId == targetOwner.User.OrganizationId
                && r.Name == Role.DefaultUserName, Ct);
            db.OrganizationMembers.Add(OrganizationMember.Create(targetOwner.User.OrganizationId, source.User.Id,
                role, source.User.DisplayName, source.User.Email, Now, authorizationVersion: 3));
            await db.SaveChangesAsync(Ct);
        }
        var request = Request(source, receiver) with { RecipientOrganizationId = targetOwner.User.OrganizationId };

        // When
        var (offered, operation) = await apiFactory.CreateAuthenticatedClient(source.User)
            .POSTAsync<CreateSharedUnlockOperationEndpoint, CreateSharedUnlockOperationRequest, SharedUnlockOperationResponse>(request);
        offered.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ConsumeAsync(operation, receiver)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        if (removedBeforeCommit)
        {
            await using var scope = apiFactory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
            var member = await db.OrganizationMembers.SingleAsync(m => m.UserId == source.User.Id
                && m.OrganizationId == targetOwner.User.OrganizationId, Ct);
            member.RequestRemoval(Guid.NewGuid(), targetOwner.User.Id, Now);
            await db.SaveChangesAsync(Ct);
        }
        var (committed, result) = await CommitAsync(operation, receiver);

        // Then
        committed.StatusCode.ShouldBe(removedBeforeCommit ? HttpStatusCode.Conflict : HttpStatusCode.OK);
        await using var verification = apiFactory.Services.CreateAsyncScope();
        var read = verification.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        var receiverSessions = await read.RefreshTokens.Where(t => t.UserId == source.User.Id
            && t.OrganizationId == targetOwner.User.OrganizationId).ToListAsync(Ct);
        receiverSessions.Count.ShouldBe(removedBeforeCommit ? 0 : 1);
        if (!removedBeforeCommit)
        {
            result.Context.OrganizationId.ShouldBe(targetOwner.User.OrganizationId);
            result.Context.AuthorizationVersion.ShouldBe(3u);
            receiverSessions[0].AuthorizationVersion.ShouldBe(3u);
            var inherited = await read.SharedUnlockAuthorizations.SingleAsync(a => a.UserId == source.User.Id && a.Id == result.AuthorizationId, Ct);
            inherited.OrganizationId.ShouldBe(targetOwner.User.OrganizationId);
            inherited.AuthorizationVersion.ShouldBe(3u);
        }
    }

    [Fact]
    public async Task When_OffCommitsAfterSessionPreparation_Then_ReceiverTokensAndAuthorityRollBackTogether()
    {
        // Given
        var source = await SeedSourceAsync();
        using var receiver = Key.Create(SignatureAlgorithm.Ed25519);
        var offered = await OfferAsync(source, receiver);
        (await ConsumeAsync(offered, receiver)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var attempt = scope.ServiceProvider.GetRequiredService<IdentityDomainWriteContext>();
        var operation = await attempt.SharedUnlockOperations.SingleAsync(o => o.Id == offered.Context.OperationId, Ct);
        var authority = await SharedUnlockOperationAuthority.LoadAsync(attempt, operation, Now, Ct);
        authority.ShouldNotBeNull();
        var issued = scope.ServiceProvider.GetRequiredService<IAuthSessionIssuer>().IssueWithSession(
            authority.User, authority.TargetOrganization, authority.TargetMember.EffectivePermissions(),
            authority.TargetMember.AuthorizationVersion, Now, authority.Source.SecondFactorRevision, authority.Source.SecondFactorVerifiedAt);
        operation.TryCommit(issued.Session.SessionId!.Value, Now).ShouldBeTrue();
        attempt.Add(SharedUnlockAuthorization.Inherit(Guid.NewGuid(), issued.Session, authority.Source, operation));
        authority.Fence(attempt);

        // When
        await using (var mutation = apiFactory.Services.CreateAsyncScope())
        {
            var db = mutation.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
            (await db.Users.SingleAsync(u => u.Id == source.User.Id, Ct)).TrySetSharedUnlockPreference(false, 1, Now).ShouldBeTrue();
            await db.SaveChangesAsync(Ct);
        }

        // Then
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => attempt.CommitAsync(Ct));
        await using var verification = apiFactory.Services.CreateAsyncScope();
        var read = verification.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        (await read.RefreshTokens.CountAsync(t => t.UserId == source.User.Id, Ct)).ShouldBe(1);
        (await read.SharedUnlockAuthorizations.CountAsync(a => a.UserId == source.User.Id, Ct)).ShouldBe(1);
        (await read.SharedUnlockOperations.SingleAsync(o => o.Id == operation.Id, Ct)).State.ShouldBe(SharedUnlockOperationState.Consumed);
    }

    [Fact]
    public async Task When_CleanupCrossesSeveralPages_Then_OnlyExpiredOperationMetadataIsRemoved()
    {
        // Given
        var source = await SeedSourceAsync();
        using var receiver = Key.Create(SignatureAlgorithm.Ed25519);
        var liveId = Guid.NewGuid();
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
            var user = await db.Users.SingleAsync(u => u.Id == source.User.Id, Ct);
            var session = await db.RefreshTokens.SingleAsync(t => t.UserId == user.Id, Ct);
            var org = await db.Organizations.SingleAsync(o => o.Id == user.OrganizationId, Ct);
            var member = await db.OrganizationMembers.SingleAsync(m => m.UserId == user.Id, Ct);
            for (var i = 0; i < 4; i++)
            {
                var operation = SharedUnlockOperation.Create(i == 3 ? liveId : Guid.NewGuid(), user,
                    source.Authorization, session, org, org, member, source.Link,
                    new SharedUnlockChannel(SharedUnlockDirection.WebToExtension, "https://api.example.test",
                        "https://app.example.test", "synthetic-extension", "synthetic-document", SourceGeneration,
                        RecipientGeneration, SourceGeneration, RecipientGeneration, receiver.PublicKey.Export(KeyBlobFormat.RawPublicKey)),
                    SharedUnlockKeyContextDigest.Hash(SharedUnlockKeyContextDigest.FromUser(user)!), new byte[32], Now);
                operation.BindTranscriptHash(SharedUnlockTranscript.Hash(operation));
                db.SharedUnlockOperations.Add(operation);
                if (i < 3) { db.Entry(operation).Property(o => o.ExpiresAt).CurrentValue = Now; }
            }
            await db.SaveChangesAsync(Ct);
        }

        // When
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            await new CleanupSharedUnlockOperationsJob(scope.ServiceProvider.GetRequiredService<IdentityDomainWriteContext>(),
                Options.Create(new CleanupSharedUnlockOperationsJobOptions { BatchSize = 2 }), apiFactory.FakeClock).ExecuteAsync(Ct);
        }

        // Then
        await using var verification = apiFactory.Services.CreateAsyncScope();
        var read = verification.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        (await read.SharedUnlockOperations.Where(o => o.UserId == source.User.Id).Select(o => o.Id).ToListAsync(Ct)).ShouldBe([liveId]);
        (await read.RefreshTokens.CountAsync(t => t.UserId == source.User.Id, Ct)).ShouldBe(1);
        (await read.SharedUnlockAuthorizations.CountAsync(a => a.UserId == source.User.Id, Ct)).ShouldBe(1);
        (await read.SharedUnlockLinks.CountAsync(l => l.UserId == source.User.Id, Ct)).ShouldBe(1);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("consume")]
    [InlineData("commit")]
    public async Task When_EmergencySwitchIsDisabled_Then_NoNewReceiverSessionIsIssuedAndOwnSessionSurvives(string stage)
    {
        // Given
        var source = await SeedSourceAsync();
        using var receiver = Key.Create(SignatureAlgorithm.Ed25519);
        SharedUnlockOperationResponse? operation = null;
        if (stage != "create") { operation = await OfferAsync(source, receiver); }
        if (stage == "commit")
        {
            (await ConsumeAsync(operation!, receiver)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        var monitor = apiFactory.Services.GetRequiredService<IOptionsMonitor<SharedUnlockOptions>>();
        var cache = apiFactory.Services.GetRequiredService<IOptionsMonitorCache<SharedUnlockOptions>>();
        var original = monitor.CurrentValue;
        cache.TryRemove(Options.DefaultName);
        cache.TryAdd(Options.DefaultName, new SharedUnlockOptions { Enabled = false });

        // When
        HttpStatusCode status;
        HttpStatusCode ownSessionStatus;
        try
        {
            if (stage == "create")
            {
                var (response, _) = await apiFactory.CreateAuthenticatedClient(source.User)
                    .POSTAsync<CreateSharedUnlockOperationEndpoint, CreateSharedUnlockOperationRequest, SharedUnlockOperationResponse>(Request(source, receiver));
                status = response.StatusCode;
            }
            else
            {
                status = stage == "consume" ? (await ConsumeAsync(operation!, receiver)).Response.StatusCode
                    : (await CommitAsync(operation!, receiver)).Response.StatusCode;
            }
            var (refreshed, _) = await apiFactory.CreateClient().POSTAsync<RefreshAccessTokenEndpoint,
                RefreshAccessTokenRequest, RefreshAccessTokenResponse>(new() { RefreshToken = source.RawToken });
            ownSessionStatus = refreshed.StatusCode;
        }
        finally
        {
            cache.TryRemove(Options.DefaultName);
            cache.TryAdd(Options.DefaultName, original);
        }

        // Then
        status.ShouldBe(HttpStatusCode.ServiceUnavailable);
        ownSessionStatus.ShouldBe(HttpStatusCode.OK);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var read = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        (await read.SharedUnlockAuthorizations.CountAsync(a => a.UserId == source.User.Id, Ct)).ShouldBe(1);
        (await read.SharedUnlockOperations.AnyAsync(o => o.UserId == source.User.Id && o.State == SharedUnlockOperationState.Committed, Ct)).ShouldBeFalse();
        (await read.Users.SingleAsync(u => u.Id == source.User.Id, Ct)).SharedUnlockEnabled.ShouldBeTrue();
    }

    private Instant Now => Instant.FromUnixTimeMilliseconds(apiFactory.FakeClock.GetCurrentInstant().ToUnixTimeMilliseconds());
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static byte[] SourceGeneration => Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static byte[] RecipientGeneration => Enumerable.Range(40, 32).Select(i => (byte)i).ToArray();
    private static byte[] NewWebGeneration => Enumerable.Range(80, 32).Select(i => (byte)i).ToArray();
    private sealed record Source(User User, string RawToken, SharedUnlockAuthorization Authorization, SharedUnlockLink Link);

    private async Task<Source> SeedSourceAsync()
    {
        apiFactory.GuidProvider.Generate().Returns(_ => Guid.NewGuid());
        var (seeded, _) = await apiFactory.Services.SeedPasswordUserAsync(SourceGeneration, emailVerified: true);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
        var user = await db.Users.SingleAsync(u => u.Id == seeded.Id, Ct);
        db.Entry(user).Property(u => u.PublicKey).CurrentValue = SourceGeneration;
        db.Entry(user).Property(u => u.MemberKeyVersion).CurrentValue = 1;
        user.TryAdvanceSharedUnlockSequence().ShouldBeTrue();
        var factor = TotpCredential.StartEnrollment(user.Id, "synthetic-factor", Now - Duration.FromMinutes(5));
        factor.Confirm(0, Now - Duration.FromMinutes(5));
        db.TotpCredentials.Add(factor);
        var raw = Guid.NewGuid().ToString("N");
        var session = RefreshToken.Create(Guid.NewGuid(), user.Id, user.OrganizationId,
            TokenService.HashToken(raw), 1, Now + Duration.FromDays(1), Now,
            factor.ConfigurationRevision, Now - Duration.FromMinutes(5));
        db.RefreshTokens.Add(session);
        var root = SharedUnlockAuthorization.Create(user.Id, session.SessionId!.Value);
        root.AuthorizeManualUnlock(Guid.NewGuid(), user, session, SourceGeneration,
            Now + Duration.FromMinutes(15), Now + Duration.FromHours(8), Now + Duration.FromHours(1), Now);
        var link = SharedUnlockLink.Create(user.Id, Guid.NewGuid(), Now);
        link.TryActivateFromManualUnlock(1, root.Sequence, Now).ShouldBeTrue();
        root.TryBind(link).ShouldBeTrue();
        db.SharedUnlockLinks.Add(link);
        db.SharedUnlockAuthorizations.Add(root);
        await db.SaveChangesAsync(Ct);
        return new Source(user, raw, root, link);
    }

    private static CreateSharedUnlockOperationRequest Request(Source source, Key receiver, string direction = "web-to-extension") => new()
    {
        RefreshToken = source.RawToken, AuthorizationId = source.Authorization.Id, LinkId = source.Link.Id,
        LinkEpoch = source.Link.Epoch, ExpectedPreferenceRevision = 1, RecipientOrganizationId = source.User.OrganizationId,
        Direction = direction, ApiOrigin = "https://api.example.test", WebOrigin = "https://app.example.test",
        ExtensionId = "synthetic-extension", DocumentBinding = "synthetic-browser-document",
        WebGeneration = direction == "web-to-extension" ? SourceGeneration : RecipientGeneration,
        ExtensionGeneration = direction == "web-to-extension" ? RecipientGeneration : SourceGeneration,
        SourcePublicKey = SourceGeneration, RecipientPublicKey = RecipientGeneration,
        RecipientProofPublicKey = receiver.PublicKey.Export(KeyBlobFormat.RawPublicKey),
    };

    private async Task<SharedUnlockOperationResponse> OfferAsync(Source source, Key receiver, string direction = "web-to-extension")
    {
        var (response, result) = await apiFactory.CreateAuthenticatedClient(source.User)
            .POSTAsync<CreateSharedUnlockOperationEndpoint, CreateSharedUnlockOperationRequest, SharedUnlockOperationResponse>(Request(source, receiver, direction));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        return result;
    }

    private async Task<(HttpResponseMessage Response, SharedUnlockOperationResponse Result)> ConsumeAsync(SharedUnlockOperationResponse operation, Key receiver)
    {
        var (response, result) = await apiFactory.CreateClient().POSTAsync<ConsumeSharedUnlockOperationEndpoint, ConsumeSharedUnlockOperationRequest, SharedUnlockOperationResponse>(new()
        { OperationId = operation.Context.OperationId, Signature = Sign(Proof(operation), receiver, SharedUnlockProofPurpose.Consume) });
        return (response, result);
    }

    private async Task<(HttpResponseMessage Response, CommitSharedUnlockOperationResponse Result)> CommitAsync(SharedUnlockOperationResponse operation, Key receiver)
    {
        var (response, result) = await apiFactory.CreateClient().POSTAsync<CommitSharedUnlockOperationEndpoint, CommitSharedUnlockOperationRequest, CommitSharedUnlockOperationResponse>(new()
        { OperationId = operation.Context.OperationId, Signature = Sign(Proof(operation), receiver, SharedUnlockProofPurpose.Commit) });
        return (response, result);
    }

    private static SharedUnlockProofAuthority Proof(SharedUnlockOperationResponse operation) => new(operation.Context.OperationId,
        Base64Url.DecodeFromChars(operation.Challenge), Base64Url.DecodeFromChars(operation.TranscriptHash),
        Base64Url.DecodeFromChars(operation.RecipientProofPublicKey), Instant.FromUnixTimeMilliseconds(operation.Context.IssuedAtMs),
        Instant.FromUnixTimeMilliseconds(operation.Context.ExpiresAtMs));

    private static byte[] Sign(SharedUnlockProofAuthority authority, Key receiver, SharedUnlockProofPurpose purpose) =>
        SignatureAlgorithm.Ed25519.Sign(receiver, SharedUnlockIdentityProof.Encode(authority, purpose));
}
