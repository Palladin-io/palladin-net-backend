using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Palladin.Core.Json;
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
    [InlineData("web-to-extension", "idle")]
    [InlineData("web-to-extension", "absolute")]
    [InlineData("web-to-extension", "offline")]
    [InlineData("extension-to-web", "idle")]
    [InlineData("extension-to-web", "absolute")]
    [InlineData("extension-to-web", "offline")]
    public async Task When_ClientHasShorterLimit_Then_CommitAndNextHandoffPreserveIt(string direction, string limit)
    {
        // Given
        var source = await SeedSourceAsync();
        using var receiver = Key.Create(SignatureAlgorithm.Ed25519);
        var cap = Now + Duration.FromSeconds(20);
        var request = Request(source, receiver, direction);
        request = limit switch
        {
            "idle" => request with { IdleDeadlineMs = cap.ToUnixTimeMilliseconds() },
            "absolute" => request with { AbsoluteDeadlineMs = cap.ToUnixTimeMilliseconds() },
            _ => request with { OfflineDeadlineMs = cap.ToUnixTimeMilliseconds() },
        };

        // When
        var (offered, operation) = await apiFactory.CreateAuthenticatedClient(source.User)
            .POSTAsync<CreateSharedUnlockOperationEndpoint, CreateSharedUnlockOperationRequest, SharedUnlockOperationResponse>(request);
        offered.StatusCode.ShouldBe(HttpStatusCode.OK);
        operation.Context.ExpiresAtMs.ShouldBe(cap.ToUnixTimeMilliseconds());
        (await ConsumeAsync(operation, receiver)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (committed, first) = await CommitAsync(operation, receiver);
        committed.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var nextReceiver = Key.Create(SignatureAlgorithm.Ed25519);
        var reverse = direction == "web-to-extension" ? "extension-to-web" : "web-to-extension";
        var nextRequest = Request(source, nextReceiver, reverse) with
        {
            RefreshToken = first.Session.RefreshToken, AuthorizationId = first.AuthorizationId,
            ExtensionGeneration = direction == "web-to-extension" ? RecipientGeneration : NewWebGeneration,
            WebGeneration = direction == "web-to-extension" ? NewWebGeneration : RecipientGeneration,
        };
        var nextClient = apiFactory.CreateClient();
        nextClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", first.Session.AccessToken);
        var (nextOffered, next) = await nextClient
            .POSTAsync<CreateSharedUnlockOperationEndpoint, CreateSharedUnlockOperationRequest, SharedUnlockOperationResponse>(nextRequest);
        nextOffered.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ConsumeAsync(next, nextReceiver)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (nextCommitted, final) = await CommitAsync(next, nextReceiver);

        // Then
        nextCommitted.StatusCode.ShouldBe(HttpStatusCode.OK);
        final.Context.UnlockedAtMs.ShouldBe(source.Authorization.UnlockedAt.ToUnixTimeMilliseconds());
        final.Context.IdleDeadlineMs.ShouldBe(limit is "idle" or "absolute" ? cap.ToUnixTimeMilliseconds() : request.IdleDeadlineMs);
        final.Context.AbsoluteDeadlineMs.ShouldBe(request.AbsoluteDeadlineMs);
        final.Context.OfflineDeadlineMs.ShouldBe(request.OfflineDeadlineMs);
        final.Context.ExpiresAtMs.ShouldBe(cap.ToUnixTimeMilliseconds());
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        var inherited = await db.SharedUnlockAuthorizations.SingleAsync(a => a.Id == final.AuthorizationId, Ct);
        inherited.SecondFactorVerifiedAt.ShouldBe(source.Authorization.SecondFactorVerifiedAt);
        inherited.SecondFactorRevision.ShouldBe(source.Authorization.SecondFactorRevision);
        inherited.Sequence.ShouldBe(source.Authorization.Sequence);
        var original = await db.SharedUnlockAuthorizations.SingleAsync(a => a.Id == source.Authorization.Id, Ct);
        original.IdleDeadline.ShouldBe(source.Authorization.IdleDeadline);
        original.AbsoluteDeadline.ShouldBe(source.Authorization.AbsoluteDeadline);
        original.OfflineDeadline.ShouldBe(source.Authorization.OfflineDeadline);
    }

    [Theory]
    [InlineData("idle")]
    [InlineData("absolute")]
    [InlineData("offline")]
    public async Task When_ClientLimitIsExpired_Then_NoOperationOrReceiverSessionIsCreated(string limit)
    {
        // Given
        var source = await SeedSourceAsync();
        using var receiver = Key.Create(SignatureAlgorithm.Ed25519);
        var request = Request(source, receiver);
        request = limit switch
        {
            "idle" => request with { IdleDeadlineMs = Now.ToUnixTimeMilliseconds() },
            "absolute" => request with { AbsoluteDeadlineMs = Now.ToUnixTimeMilliseconds() },
            _ => request with { OfflineDeadlineMs = Now.ToUnixTimeMilliseconds() },
        };

        // When
        var (response, _) = await apiFactory.CreateAuthenticatedClient(source.User)
            .POSTAsync<CreateSharedUnlockOperationEndpoint, CreateSharedUnlockOperationRequest, SharedUnlockOperationResponse>(request);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        (await db.SharedUnlockOperations.AnyAsync(o => o.UserId == source.User.Id, Ct)).ShouldBeFalse();
        (await db.RefreshTokens.CountAsync(t => t.UserId == source.User.Id, Ct)).ShouldBe(1);
    }

    [Theory]
    [InlineData("web-to-extension")]
    [InlineData("extension-to-web")]
    public async Task When_SharedClampFixturesReachProvider_Then_OperationAndReceiverMatchIndependentExpectedLimits(string direction)
    {
        // Given
        using var fixture = ReadOperationRequests();
        var fixtureNow = fixture.RootElement.GetProperty("nowMs").GetInt64();
        var offset = Now.ToUnixTimeMilliseconds() - fixtureNow;
        var source = await SeedSourceAsync();
        var root = fixture.RootElement.GetProperty("sourceAuthorization");
        source.Authorization.UnlockedAt.ToUnixTimeMilliseconds().ShouldBe(root.GetProperty("unlockedAtMs").GetInt64() + offset);
        source.Authorization.IdleDeadline.ToUnixTimeMilliseconds().ShouldBe(root.GetProperty("idleDeadlineMs").GetInt64() + offset);
        source.Authorization.AbsoluteDeadline.ToUnixTimeMilliseconds().ShouldBe(root.GetProperty("absoluteDeadlineMs").GetInt64() + offset);
        source.Authorization.OfflineDeadline.ToUnixTimeMilliseconds().ShouldBe(root.GetProperty("offlineDeadlineMs").GetInt64() + offset);
        foreach (var vector in fixture.RootElement.GetProperty("positive").EnumerateArray())
        {
            var expiry = vector.TryGetProperty("sourceRefreshExpiresAtMs", out var overrideExpiry)
                ? overrideExpiry.GetInt64() : fixture.RootElement.GetProperty("sourceRefreshExpiresAtMs").GetInt64();
            await using (var setup = apiFactory.Services.CreateAsyncScope())
            {
                var db = setup.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
                var session = await db.RefreshTokens.SingleAsync(t => t.Id == source.Authorization.SessionId, Ct);
                db.Entry(session).Property(t => t.ExpiresAt).CurrentValue = Instant.FromUnixTimeMilliseconds(expiry + offset);
                await db.SaveChangesAsync(Ct);
            }
            using var receiver = Key.Create(SignatureAlgorithm.Ed25519);
            var deadlines = vector.GetProperty("requestDeadlines");
            var request = Request(source, receiver, direction) with
            {
                IdleDeadlineMs = deadlines.GetProperty("idleDeadlineMs").GetInt64() + offset,
                AbsoluteDeadlineMs = deadlines.GetProperty("absoluteDeadlineMs").GetInt64() + offset,
                OfflineDeadlineMs = deadlines.GetProperty("offlineDeadlineMs").GetInt64() + offset,
            };

            // When
            var (offered, operation) = await apiFactory.CreateAuthenticatedClient(source.User)
                .POSTAsync<CreateSharedUnlockOperationEndpoint, CreateSharedUnlockOperationRequest, SharedUnlockOperationResponse>(request);
            offered.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await ConsumeAsync(operation, receiver)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var (committed, result) = await CommitAsync(operation, receiver);

            // Then
            committed.StatusCode.ShouldBe(HttpStatusCode.OK);
            var expected = vector.GetProperty("expectedDeadlines");
            result.Context.ShouldBe(operation.Context);
            result.Context.IdleDeadlineMs.ShouldBe(expected.GetProperty("idleDeadlineMs").GetInt64() + offset);
            result.Context.AbsoluteDeadlineMs.ShouldBe(expected.GetProperty("absoluteDeadlineMs").GetInt64() + offset);
            result.Context.OfflineDeadlineMs.ShouldBe(expected.GetProperty("offlineDeadlineMs").GetInt64() + offset);
            result.Context.ExpiresAtMs.ShouldBe(expected.GetProperty("expiresAtMs").GetInt64() + offset);
            await using var scope = apiFactory.Services.CreateAsyncScope();
            var read = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
            var inherited = await read.SharedUnlockAuthorizations.SingleAsync(a => a.Id == result.AuthorizationId, Ct);
            inherited.IdleDeadline.ToUnixTimeMilliseconds().ShouldBe(result.Context.IdleDeadlineMs);
            inherited.AbsoluteDeadline.ToUnixTimeMilliseconds().ShouldBe(result.Context.AbsoluteDeadlineMs);
            inherited.OfflineDeadline.ToUnixTimeMilliseconds().ShouldBe(result.Context.OfflineDeadlineMs);
        }
    }

    [Theory]
    [InlineData("web-to-extension")]
    [InlineData("extension-to-web")]
    public async Task When_SharedRejectionFixturesReachProvider_Then_NoOperationOrReceiverIsCreated(string direction)
    {
        // Given
        using var fixture = ReadOperationRequests();
        var fixtureNow = fixture.RootElement.GetProperty("nowMs").GetInt64();
        var offset = Now.ToUnixTimeMilliseconds() - fixtureNow;
        var source = await SeedSourceAsync();
        using var receiver = Key.Create(SignatureAlgorithm.Ed25519);
        var client = apiFactory.CreateAuthenticatedClient(source.User);
        foreach (var vector in fixture.RootElement.GetProperty("negative").EnumerateArray())
        {
            var request = JsonSerializer.SerializeToNode(Request(source, receiver, direction), PalladinJsonSerializationSettings.DefaultOptions)!.AsObject();
            foreach (var field in new[] { "idleDeadlineMs", "absoluteDeadlineMs", "offlineDeadlineMs" })
            {
                request.Remove(field);
            }
            foreach (var field in vector.GetProperty("requestDeadlines").EnumerateObject())
            {
                request[field.Name] = field.Value.ValueKind == JsonValueKind.Number
                    && field.Value.GetDouble() >= fixtureNow
                    && field.Value.GetDouble() <= fixture.RootElement.GetProperty("sourceRefreshExpiresAtMs").GetDouble()
                    ? JsonValue.Create(field.Value.GetDouble() + offset)
                    : JsonNode.Parse(field.Value.GetRawText());
            }

            // When
            var response = await client.PostAsJsonAsync("api/account/shared-unlock/operations", request, Ct);

            // Then
            ((int)response.StatusCode).ShouldBe(vector.GetProperty("expectedStatus").GetInt32(), vector.GetProperty("name").GetString());
        }
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var read = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        (await read.SharedUnlockOperations.AnyAsync(o => o.UserId == source.User.Id, Ct)).ShouldBeFalse();
        (await read.RefreshTokens.CountAsync(t => t.UserId == source.User.Id, Ct)).ShouldBe(1);
    }

    private static JsonDocument ReadOperationRequests() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "SharedUnlock", "operation-request-v1.json")));

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
    [InlineData(false, "ttl")]
    [InlineData(true, "ttl")]
    [InlineData(false, "idle")]
    [InlineData(true, "idle")]
    [InlineData(false, "absolute")]
    [InlineData(true, "absolute")]
    [InlineData(false, "offline")]
    [InlineData(true, "offline")]
    public async Task When_OperationReachesExactExpiry_Then_ConsumeOrCommitCannotIssueSession(bool afterConsume, string limit)
    {
        // Given
        var originalTime = apiFactory.FakeClock.GetCurrentInstant();
        var source = await SeedSourceAsync();
        using var receiver = Key.Create(SignatureAlgorithm.Ed25519);
        var request = Request(source, receiver);
        var cap = (Now + Duration.FromSeconds(10)).ToUnixTimeMilliseconds();
        request = limit switch
        {
            "idle" => request with { IdleDeadlineMs = cap },
            "absolute" => request with { AbsoluteDeadlineMs = cap },
            "offline" => request with { OfflineDeadlineMs = cap },
            _ => request,
        };
        var (offered, operation) = await apiFactory.CreateAuthenticatedClient(source.User)
            .POSTAsync<CreateSharedUnlockOperationEndpoint, CreateSharedUnlockOperationRequest, SharedUnlockOperationResponse>(request);
        offered.StatusCode.ShouldBe(HttpStatusCode.OK);
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
                    SharedUnlockKeyContextDigest.Hash(SharedUnlockKeyContextDigest.FromUser(user)!), new byte[32],
                    source.Authorization.IdleDeadline, source.Authorization.AbsoluteDeadline, source.Authorization.OfflineDeadline, Now);
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
        IdleDeadlineMs = source.Authorization.IdleDeadline.ToUnixTimeMilliseconds(),
        AbsoluteDeadlineMs = source.Authorization.AbsoluteDeadline.ToUnixTimeMilliseconds(),
        OfflineDeadlineMs = source.Authorization.OfflineDeadline.ToUnixTimeMilliseconds(),
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
