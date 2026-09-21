using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using FastEndpoints;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NodaTime;
using Palladin.Core.Types;
using Palladin.Module.Audit.Contracts.ValueObjects;
using Palladin.Module.Audit.Infrastructure.Persistence;
using Palladin.Module.Notification.Infrastructure.Persistence;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Sharing;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Mocks;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault;

[Collection<ApiFactoryCollection>]
public sealed class EntrySharingReceiverTests(ApiFactory apiFactory) : TestBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task When_ARecipientOpensTheLink_Then_LinkPolicyIsSeparateFromSessionExpiry(bool named)
    {
        // Given
        var seed = await SeedAsync(named: named);
        apiFactory.MockId(Guid.NewGuid());

        // When
        var response = await apiFactory.CreateClient().POSTAsync<OpenEntryShareSessionEndpoint, OpenEntryShareSessionRequest>(
            new OpenEntryShareSessionRequest { ShareId = seed.ShareId, AccessToken = seed.AccessToken });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var body = json.RootElement;
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var share = await scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>().EntryShares
            .SingleAsync(x => x.Id == seed.ShareId, TestContext.Current.CancellationToken);
        body.GetProperty("shareExpiresAt").GetDateTimeOffset().ShouldBe(share.ExpiresAt.ToDateTimeOffset());
        body.GetProperty("expiresAt").GetDateTimeOffset().ShouldBeLessThan(body.GetProperty("shareExpiresAt").GetDateTimeOffset());
        body.GetProperty("maximumReceipts").GetInt32().ShouldBe(share.MaximumReceipts);
        body.GetProperty("otpRetryAfterSeconds").GetInt32().ShouldBe(0);
        body.EnumerateObject().Select(x => x.Name).Order().ShouldBe(new[]
        {
            "sessionId", "sessionToken", "expiresAt", "recipientMode", "protection",
            "shareExpiresAt", "maximumReceipts", "otpRetryAfterSeconds",
        }.Order());
        share.DeliveryCount.ShouldBe(0);
    }

    [Fact]
    public async Task When_AnotherSessionOpensDuringOtpCooldown_Then_ItReceivesTheSharedRemainingTime()
    {
        // Given
        var seed = await SeedAsync(named: true);
        var first = await OpenAsync(seed);
        var client = apiFactory.CreateClient();
        var issued = await client.POSTAsync<RequestEntryShareOtpEndpoint, RequestEntryShareOtpRequest>(
            new RequestEntryShareOtpRequest
            {
                ShareId = first.ShareId, SessionId = first.SessionId, SessionToken = first.SessionToken,
                Generation = 1, Language = "en",
            });
        issued.StatusCode.ShouldBe(HttpStatusCode.OK);
        apiFactory.FakeClock.Advance(Duration.FromMilliseconds(21_500));
        apiFactory.MockId(Guid.NewGuid());

        // When
        var opened = await client.POSTAsync<OpenEntryShareSessionEndpoint, OpenEntryShareSessionRequest, OpenEntryShareSessionResponse>(
            new OpenEntryShareSessionRequest { ShareId = seed.ShareId, AccessToken = seed.AccessToken });

        // Then
        opened.Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        opened.Result.OtpRetryAfterSeconds.ShouldBe(39);
        opened.Result.SessionId.ShouldNotBe(first.SessionId);
        var denied = await client.POSTAsync<RequestEntryShareOtpEndpoint, RequestEntryShareOtpRequest>(
            new RequestEntryShareOtpRequest
            {
                ShareId = seed.ShareId, SessionId = opened.Result.SessionId, SessionToken = opened.Result.SessionToken,
                Generation = 1, Language = "en",
            });
        denied.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await denied.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task When_AReceiptAndConfirmationAreRetried_Then_OneDeliveryAndOneConfirmationAreRecorded(bool notify)
    {
        // Given
        var seeded = await SeedAsync(notify: notify);
        var request = await OpenAsync(seeded);
        var client = apiFactory.CreateClient();
        var premature = await client.POSTAsync<ConfirmEntryShareReceiptEndpoint, EntryShareSessionRequest>(request);
        premature.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // When
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var delivery = await client.POSTAsync<DeliverEntryShareEndpoint, EntryShareSessionRequest, DeliverEntryShareResponse>(request);
            delivery.Response.StatusCode.ShouldBe(HttpStatusCode.OK);
            delivery.Response.Headers.CacheControl!.NoStore.ShouldBeTrue();
            delivery.Result.ShareId.ShouldBe(seeded.ShareId);
            delivery.Result.Ciphertext.ShouldBe(seeded.Ciphertext);
            var confirmed = await client.POSTAsync<ConfirmEntryShareReceiptEndpoint, EntryShareSessionRequest>(request);
            confirmed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
        var share = await db.EntryShares.SingleAsync(x => x.Id == seeded.ShareId, TestContext.Current.CancellationToken);
        share.DeliveryCount.ShouldBe(1);
        share.FirstConfirmedAt.ShouldNotBeNull();
        var activities = await db.EntryShareActivities.Where(x => x.ShareId == share.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
        activities.Count(x => x.Kind == EntryShareActivityKind.Delivered).ShouldBe(1);
        activities.Where(x => x.Kind == EntryShareActivityKind.Confirmed).ShouldHaveSingleItem().NotifySender.ShouldBe(notify);
        var consumed = await client.POSTAsync<OpenEntryShareSessionEndpoint, OpenEntryShareSessionRequest>(
            new OpenEntryShareSessionRequest { ShareId = share.Id, AccessToken = seeded.AccessToken });
        consumed.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await AssertReceiptConsumersAsync(share, notify);
    }

    private async Task AssertReceiptConsumersAsync(EntryShare share, bool notify)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            await using var scope = apiFactory.Services.CreateAsyncScope();
            var audit = scope.ServiceProvider.GetRequiredService<AuditDbReadContext>();
            var rows = await audit.AuditLogEntries.Where(x => x.OrganizationId == share.OrganizationId
                && x.EntryId == share.EntryId && x.EventType.StartsWith("entry-share."))
                .ToListAsync(deadline.Token);
            var notification = scope.ServiceProvider.GetRequiredService<NotificationDbReadContext>();
            var inbox = await notification.InboxItems.Where(x => x.OrganizationId == share.OrganizationId
                && x.SubjectId == share.Id).ToListAsync(deadline.Token);
            if (rows.Count >= 3 && (!notify || inbox.Count > 0))
            {
                rows.Count.ShouldBe(3);
                rows.Single(x => x.EventType == AuditEventType.EntryShareConfirmed)
                    .ActorType.ShouldBe(AuditActorType.ExternalRecipient);
                inbox.Count.ShouldBe(notify ? 1 : 0);
                inbox.ShouldAllBe(x => x.UserId == share.CreatedBy && x.Type == NotificationType.EntryShareReceived);
                return;
            }

            await Task.Delay(25, deadline.Token);
        }
    }

    [Theory]
    [InlineData(EntryShareProtection.Pin, "493827")]
    [InlineData(EntryShareProtection.Password, "synthetic passphrase")]
    public async Task When_OptionalProtectionIsRequired_Then_OnlyCorrectVerificationPermitsDelivery(
        EntryShareProtection protection, string secret)
    {
        // Given
        var seeded = await SeedAsync(protection, secret);
        var request = await OpenAsync(seeded);
        var client = apiFactory.CreateClient();
        (await client.POSTAsync<DeliverEntryShareEndpoint, EntryShareSessionRequest>(request)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // When
        var wrong = await VerifyAsync(request, "99999999");
        var correct = await VerifyAsync(request, secret);
        var delivered = await client.POSTAsync<DeliverEntryShareEndpoint, EntryShareSessionRequest>(request);

        // Then
        wrong.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        correct.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        delivered.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var share = await scope.ServiceProvider.GetRequiredService<VaultDbReadContext>().EntryShares
            .SingleAsync(x => x.Id == seeded.ShareId, TestContext.Current.CancellationToken);
        share.FailedAttempts.ShouldBe(1);
        share.DeliveryCount.ShouldBe(1);
    }

    [Fact]
    public async Task When_EmailVerificationIsStillRequired_Then_TheCorrectPinCannotDeliverOrEndTheLink()
    {
        // Given
        var seeded = await SeedAsync(EntryShareProtection.Pin, "493827", named: true);
        var request = await OpenAsync(seeded);

        // When
        (await VerifyAsync(request, "493827")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var client = apiFactory.CreateClient();
        var delivery = await client.POSTAsync<DeliverEntryShareEndpoint, EntryShareSessionRequest>(request);
        var ended = await client.POSTAsync<EndEntryShareEndpoint, EntryShareSessionRequest>(request);

        // Then
        delivery.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        ended.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var share = await scope.ServiceProvider.GetRequiredService<VaultDbReadContext>().EntryShares
            .SingleAsync(x => x.Id == seeded.ShareId, TestContext.Current.CancellationToken);
        share.DeliveryCount.ShouldBe(0);
        share.RevokedAt.ShouldBeNull();
    }

    [Fact]
    public async Task When_AnAuthorizedRecipientEndsTheLink_Then_TheSourceRemainsAndEverySessionLosesDelivery()
    {
        // Given
        var seeded = await SeedAsync(EntryShareProtection.Pin, "493827");
        var first = await OpenAsync(seeded);
        var second = await OpenAsync(seeded);
        var client = apiFactory.CreateClient();
        (await client.POSTAsync<EndEntryShareEndpoint, EntryShareSessionRequest>(first)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await VerifyAsync(first, "493827")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // When
        var ended = await client.POSTAsync<EndEntryShareEndpoint, EntryShareSessionRequest>(first);
        var blocked = await client.POSTAsync<DeliverEntryShareEndpoint, EntryShareSessionRequest>(second);

        // Then
        ended.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        blocked.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var share = await db.EntryShares.SingleAsync(x => x.Id == seeded.ShareId, TestContext.Current.CancellationToken);
        share.Ciphertext.ShouldBeEmpty();
        share.RevocationReason.ShouldBe(EntryShareActivityKind.EndedByRecipient);
        (await db.Entries.AnyAsync(x => x.OrganizationId == share.OrganizationId && x.VaultId == share.VaultId
            && x.Id == share.EntryId, TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [Fact]
    public async Task When_PinGuessesUseDifferentSessions_Then_TheyShareOneLockoutBudget()
    {
        // Given
        var seeded = await SeedAsync(EntryShareProtection.Pin, "493827");
        var requests = new List<EntryShareSessionRequest>();
        var options = apiFactory.Services.GetRequiredService<IOptions<EntrySharingOptions>>().Value;
        for (var index = 0; index <= options.FailedAttemptLimit; index++)
        {
            requests.Add(await OpenAsync(seeded));
        }

        // When
        foreach (var request in requests.Take(options.FailedAttemptLimit))
        {
            (await VerifyAsync(request, "999999")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        var correctAfterLockout = await VerifyAsync(requests[^1], "493827");

        // Then
        correctAfterLockout.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var share = await scope.ServiceProvider.GetRequiredService<VaultDbReadContext>().EntryShares
            .SingleAsync(x => x.Id == seeded.ShareId, TestContext.Current.CancellationToken);
        share.FailedAttempts.ShouldBe(options.FailedAttemptLimit);
        share.LockedUntil.ShouldNotBeNull();
        share.DeliveryCount.ShouldBe(0);
    }

    [Theory]
    [InlineData("policy")]
    [InlineData("sender")]
    [InlineData("revoked")]
    public async Task When_CurrentAuthorityChanges_Then_AnExistingSessionCannotDeliver(string change)
    {
        // Given
        var seeded = await SeedAsync();
        var request = await OpenAsync(seeded);
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>();
            var share = await context.EntryShares.SingleAsync(x => x.Id == seeded.ShareId, TestContext.Current.CancellationToken);
            if (change == "policy")
            {
                var security = scope.ServiceProvider.GetRequiredService<EntryShareSecurity>();
                share.ChangeProtection(EntryShareProtection.Pin,
                    security.CreateSecretVerifier(share.Id, EntryShareProtection.Pin, "493827"), apiFactory.FakeClock.GetCurrentInstant());
            }
            else if (change == "sender")
            {
                var authority = await context.EntryShareSenderAuthorities.SingleAsync(x => x.OrganizationId == share.OrganizationId
                    && x.UserId == share.CreatedBy, TestContext.Current.CancellationToken);
                authority.RevokeThrough(share.SenderAuthorizationVersion);
            }
            else
            {
                share.Revoke(EntryShareActivityKind.RevokedBySender, apiFactory.FakeClock.GetCurrentInstant());
            }

            await context.CommitAsync(TestContext.Current.CancellationToken);
        }

        // When
        var response = await apiFactory.CreateClient().POSTAsync<DeliverEntryShareEndpoint, EntryShareSessionRequest>(request);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("share")]
    [InlineData("session")]
    [InlineData("token")]
    public async Task When_TheSessionProofIsSubstituted_Then_TheSameUnavailableResponseContainsNoPacket(string field)
    {
        // Given
        var seeded = await SeedAsync();
        var valid = await OpenAsync(seeded);
        var request = field switch
        {
            "share" => valid with { ShareId = Guid.NewGuid() },
            "session" => valid with { SessionId = Guid.NewGuid() },
            _ => valid with { SessionToken = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32)) },
        };

        // When
        var response = await apiFactory.CreateClient().POSTAsync<DeliverEntryShareEndpoint, EntryShareSessionRequest>(request);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task When_TheActiveSessionCapIsReached_Then_NoFurtherSessionIsPersisted()
    {
        // Given
        var seeded = await SeedAsync();
        var limit = apiFactory.Services.GetRequiredService<IOptions<EntrySharingOptions>>().Value.MaximumActiveSessionsPerShare;
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>();
            var share = await context.EntryShares.SingleAsync(x => x.Id == seeded.ShareId, TestContext.Current.CancellationToken);
            for (var index = 0; index < limit; index++)
            {
                context.Add(share.OpenSession(Guid.NewGuid(), new byte[32], apiFactory.FakeClock.GetCurrentInstant(), Duration.FromMinutes(15)));
            }

            await context.CommitAsync(TestContext.Current.CancellationToken);
        }

        // When
        var response = await apiFactory.CreateClient().POSTAsync<OpenEntryShareSessionEndpoint, OpenEntryShareSessionRequest>(
            new OpenEntryShareSessionRequest { ShareId = seeded.ShareId, AccessToken = seeded.AccessToken });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await using var verification = apiFactory.Services.CreateAsyncScope();
        (await verification.ServiceProvider.GetRequiredService<VaultDbWriteContext>().EntryShareSessions
            .CountAsync(x => x.ShareId == seeded.ShareId, TestContext.Current.CancellationToken)).ShouldBe(limit);
    }

    [Fact]
    public async Task When_AScannerUsesGet_Then_NoSessionOrReceiptIsCreated()
    {
        // Given
        var seeded = await SeedAsync();
        var client = apiFactory.CreateClient();

        // When
        foreach (var suffix in new[] { "sessions", $"sessions/{Guid.NewGuid()}/delivery", $"sessions/{Guid.NewGuid()}/end" })
        {
            var response = await client.GetAsync($"/api/entry-shares/{seeded.ShareId}/{suffix}", TestContext.Current.CancellationToken);
            response.IsSuccessStatusCode.ShouldBeFalse();
            response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        }

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
        (await db.EntryShareSessions.AnyAsync(x => x.ShareId == seeded.ShareId, TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await db.EntryShares.SingleAsync(x => x.Id == seeded.ShareId, TestContext.Current.CancellationToken)).DeliveryCount.ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task When_TheBodyExceedsTheBound_Then_ItIsRejectedBeforeDeserialization(bool chunked)
    {
        // Given
        var message = new HttpRequestMessage(HttpMethod.Post, $"/api/entry-shares/{Guid.NewGuid()}/sessions")
        {
            Content = new StringContent(new string('x', 4097)),
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        message.Headers.TransferEncodingChunked = chunked;

        // When
        var response = await apiFactory.CreateClient().SendAsync(message, TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }

    private Task<HttpResponseMessage> VerifyAsync(EntryShareSessionRequest request, string secret) =>
        apiFactory.CreateClient().POSTAsync<VerifyEntryShareSecretEndpoint, VerifyEntryShareSecretRequest>(
            new VerifyEntryShareSecretRequest
            {
                ShareId = request.ShareId, SessionId = request.SessionId, SessionToken = request.SessionToken, Secret = secret,
            });

    private async Task<EntryShareSessionRequest> OpenAsync(SharingSeed seed)
    {
        apiFactory.MockId(Guid.NewGuid());
        var opened = await apiFactory.CreateClient().POSTAsync<OpenEntryShareSessionEndpoint,
            OpenEntryShareSessionRequest, OpenEntryShareSessionResponse>(
            new OpenEntryShareSessionRequest { ShareId = seed.ShareId, AccessToken = seed.AccessToken });
        opened.Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        opened.Response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var json = await opened.Response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        json.ShouldNotContain("ciphertext", Case.Insensitive);
        json.ShouldNotContain("email", Case.Insensitive);
        return new EntryShareSessionRequest
        {
            ShareId = seed.ShareId, SessionId = opened.Result.SessionId, SessionToken = opened.Result.SessionToken,
        };
    }

    private async Task<SharingSeed> SeedAsync(EntryShareProtection protection = EntryShareProtection.None,
        string? secret = null, bool named = false, bool notify = false)
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var entryId = await apiFactory.Services.SeedSyncEntryAsync(organization.Id, vault.Id, user.Id);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>();
        var security = scope.ServiceProvider.GetRequiredService<EntryShareSecurity>();
        var sourceScope = new EntryScope(organization.Id, vault.Id, entryId);
        var source = await scope.ServiceProvider.GetRequiredService<EntryShareAuthority>()
            .LoadSenderSourceAsync(sourceScope, user.Id, 1, TestContext.Current.CancellationToken);
        var id = Guid.NewGuid();
        var accessToken = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var ciphertext = RandomNumberGenerator.GetBytes(64);
        var now = Instant.FromUnixTimeMilliseconds(apiFactory.FakeClock.GetCurrentInstant().ToUnixTimeMilliseconds());
        context.Add(EntryShare.Create(id, sourceScope, source.Entry.CurrentRevision, user.Id, now,
            now + Duration.FromHours(1), 1,
            named ? EntryShareRecipientMode.NamedRecipient : EntryShareRecipientMode.AnyoneWithLink,
            named ? security.ProtectRecipientEmail(id, "recipient@example.test") : null,
            protection, security.CreateSecretVerifier(id, protection, secret), security.HashAccessToken(id, accessToken),
            RandomNumberGenerator.GetBytes(24), ciphertext, notify, 1, source.Member.AddedAt));
        await context.CommitAsync(TestContext.Current.CancellationToken);
        return new SharingSeed(id, accessToken, ciphertext);
    }

    private sealed record SharingSeed(Guid ShareId, string AccessToken, byte[] Ciphertext);
}
