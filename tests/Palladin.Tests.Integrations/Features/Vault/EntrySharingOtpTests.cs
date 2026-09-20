using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using FastEndpoints;
using MassTransit;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NodaTime;
using NSubstitute;
using Palladin.Core.Types;
using Palladin.Module.Notification.Contracts.Commands;
using Palladin.Module.Notification.Infrastructure.Email;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Sharing;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault;

[Collection<ApiFactoryCollection>]
public sealed class EntrySharingOtpTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_TheEmailCommandCrossesTheBus_Then_ItsExpiryIsPreserved()
    {
        // Given
        var seed = await SeedAsync();
        var received = new TaskCompletionSource<Instant>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var observer = apiFactory.Services.GetRequiredService<IBus>()
            .ConnectConsumeObserver(new EmailExpiryObserver(seed.Session.SessionId, received));

        // When
        (await RequestAsync(seed)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var expiresAt = await received.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Then
        expiresAt.ShouldBeGreaterThan(apiFactory.FakeClock.GetCurrentInstant());
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pl")]
    public async Task When_TheNamedRecipientRequestsAndVerifiesOtp_Then_OnlyThatSessionCanReceiveTheSnapshot(string language)
    {
        // Given
        var seed = await SeedAsync();
        var client = apiFactory.CreateClient();
        (await client.POSTAsync<DeliverEntryShareEndpoint, EntryShareSessionRequest>(seed.Session)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // When
        (await RequestAsync(seed, language: language)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var email = await ReadEmailAsync(seed);
        var code = ReadCode(email);
        (await VerifyAsync(seed, code)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await VerifyAsync(seed, code)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var delivery = await client.POSTAsync<DeliverEntryShareEndpoint, EntryShareSessionRequest>(seed.Session);

        // Then
        delivery.StatusCode.ShouldBe(HttpStatusCode.OK);
        email.Subject.ShouldNotContain(code);
        email.TextBody.ShouldNotContain(seed.Session.SessionToken);
        email.TextBody.ShouldNotContain("api/entry-shares");
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
        var session = await db.EntryShareSessions.SingleAsync(x => x.ShareId == seed.Session.ShareId
            && x.Id == seed.Session.SessionId, TestContext.Current.CancellationToken);
        session.EmailVerifiedAt.ShouldNotBeNull();
        session.OtpHash.ShouldBeNull();
        session.ProtectedOtp.ShouldBeNull();
        (await db.EntryShares.SingleAsync(x => x.Id == seed.Session.ShareId, TestContext.Current.CancellationToken)).DeliveryCount.ShouldBe(1);
    }

    [Fact]
    public async Task When_TheSameOtpRequestIsRetried_Then_TheGenerationAndCodeDoNotChange()
    {
        // Given
        var seed = await SeedAsync();

        // When
        (await RequestAsync(seed)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var email = await ReadEmailAsync(seed);
        (await RequestAsync(seed)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();
        var session = await db.EntryShareSessions.SingleAsync(x => x.Id == seed.Session.SessionId, TestContext.Current.CancellationToken);
        session.OtpGeneration.ShouldBe(1);
        scope.ServiceProvider.GetRequiredService<EntryShareSecurity>()
            .VerifyOtp(session.Id, ReadCode(email), session.OtpHash).ShouldBeTrue();
        (await VerifyAsync(seed, ReadCode(email))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task When_AResendReplacesTheCode_Then_OldGenerationsCannotVerifyOrResetTheCurrentOne()
    {
        // Given
        var seed = await SeedAsync();
        (await RequestAsync(seed)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var original = ReadCode(await ReadEmailAsync(seed));
        (await RequestAsync(seed, generation: 2)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        apiFactory.FakeClock.Advance(Duration.FromSeconds(61));

        // When
        (await RequestAsync(seed, generation: 2)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var replacement = ReadCode(await ReadEmailAsync(seed));
        var oldVerification = await VerifyAsync(seed, original, generation: 1);
        var oldRequest = await RequestAsync(seed, generation: 1);
        var currentVerification = await VerifyAsync(seed, replacement, generation: 2);

        // Then
        oldVerification.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        oldRequest.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        currentVerification.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task When_TheCodeExpires_Then_ItDoesNotVerifyOrConsumeAReceipt()
    {
        // Given
        var seed = await SeedAsync();
        (await RequestAsync(seed)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var code = ReadCode(await ReadEmailAsync(seed));
        apiFactory.FakeClock.Advance(Duration.FromSeconds(301));

        // When
        var response = await VerifyAsync(seed, code);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>().EntryShares
            .SingleAsync(x => x.Id == seed.Session.ShareId, TestContext.Current.CancellationToken)).DeliveryCount.ShouldBe(0);
    }

    [Fact]
    public async Task When_PinIsAlsoRequired_Then_VerifyingEmailDoesNotBypassIt()
    {
        // Given
        var seed = await SeedAsync(pin: true);
        (await RequestAsync(seed)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await VerifyAsync(seed, ReadCode(await ReadEmailAsync(seed)))).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // When
        var client = apiFactory.CreateClient();
        var beforePin = await client.POSTAsync<DeliverEntryShareEndpoint, EntryShareSessionRequest>(seed.Session);
        var verifiedPin = await client.POSTAsync<VerifyEntryShareSecretEndpoint, VerifyEntryShareSecretRequest>(
            new VerifyEntryShareSecretRequest
            {
                ShareId = seed.Session.ShareId, SessionId = seed.Session.SessionId,
                SessionToken = seed.Session.SessionToken, Secret = "493827",
            });
        var afterPin = await client.POSTAsync<DeliverEntryShareEndpoint, EntryShareSessionRequest>(seed.Session);

        // Then
        beforePin.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        verifiedPin.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        afterPin.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task When_GuessingOtp_Then_TheSharedAttemptBudgetBlocksEvenTheCorrectCode()
    {
        // Given
        var seed = await SeedAsync();
        (await RequestAsync(seed)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var code = ReadCode(await ReadEmailAsync(seed));
        var wrong = code == "000000" ? "111111" : "000000";
        var limit = apiFactory.Services.GetRequiredService<IOptions<EntrySharingOptions>>().Value.FailedAttemptLimit;

        // When
        for (var index = 0; index < limit; index++)
        {
            (await VerifyAsync(seed, wrong)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        var correct = await VerifyAsync(seed, code);

        // Then
        correct.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var share = await scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>().EntryShares
            .SingleAsync(x => x.Id == seed.Session.ShareId, TestContext.Current.CancellationToken);
        share.FailedAttempts.ShouldBe(limit);
        share.DeliveryCount.ShouldBe(0);
    }

    [Fact]
    public async Task When_PublicationFailsAfterOtpCommit_Then_RecoveryPublishesTheSameGenerationAndClearsPendingMaterial()
    {
        // Given
        var seed = await SeedAsync(pending: true);
        var publisher = Substitute.For<IBus>();
        publisher.Publish(Arg.Any<SendEntryShareVerificationEmailCommand>(),
                Arg.Any<IPipe<PublishContext<SendEntryShareVerificationEmailCommand>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException("synthetic publication failure")));

        // When
        await Should.ThrowAsync<IOException>(() => DispatchAsync(seed, publisher));
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var session = await scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>().EntryShareSessions
                .SingleAsync(x => x.Id == seed.Session.SessionId, TestContext.Current.CancellationToken);
            session.ProtectedOtp.ShouldNotBeNull();
            session.ProtectedOtp.ShouldNotBe("493827");
        }

        publisher.Publish(Arg.Any<SendEntryShareVerificationEmailCommand>(),
                Arg.Any<IPipe<PublishContext<SendEntryShareVerificationEmailCommand>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        await DispatchAsync(seed, publisher);
        await DispatchAsync(seed, publisher);

        // Then
        await publisher.Received(2).Publish(Arg.Is<SendEntryShareVerificationEmailCommand>(x =>
                x.SessionId == seed.Session.SessionId && x.Generation == 1 && x.Code == "493827"),
            Arg.Any<IPipe<PublishContext<SendEntryShareVerificationEmailCommand>>>(), Arg.Any<CancellationToken>());
        await using var verification = apiFactory.Services.CreateAsyncScope();
        (await verification.ServiceProvider.GetRequiredService<VaultDbWriteContext>().EntryShareSessions
            .SingleAsync(x => x.Id == seed.Session.SessionId, TestContext.Current.CancellationToken)).ProtectedOtp.ShouldBeNull();
    }

    [Fact]
    public async Task When_RecoveryFindsAnExpiredCode_Then_ItErasesPendingMaterialWithoutPublishing()
    {
        // Given
        var seed = await SeedAsync(pending: true);
        var publisher = Substitute.For<IBus>();
        apiFactory.FakeClock.Advance(Duration.FromSeconds(301));

        // When
        await DispatchAsync(seed, publisher);

        // Then
        publisher.ReceivedCalls().ShouldBeEmpty();
        await using var scope = apiFactory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>().EntryShareSessions
            .SingleAsync(x => x.Id == seed.Session.SessionId, TestContext.Current.CancellationToken)).ProtectedOtp.ShouldBeNull();
    }

    private async Task DispatchAsync(OtpSeed seed, IBus publisher)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        await new EntryShareOtpDispatcher(scope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>(),
            scope.ServiceProvider.GetRequiredService<EntryShareAuthority>(), scope.ServiceProvider.GetRequiredService<EntryShareSecurity>(),
            publisher, apiFactory.FakeClock).DispatchAsync(seed.Session.ShareId, seed.Session.SessionId, 1, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task When_AnotherSessionRequestsOrReplaysOtp_Then_TheShareCooldownAndSessionGateRemainEffective()
    {
        // Given
        var seed = await SeedAsync();
        (await RequestAsync(seed)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var code = ReadCode(await ReadEmailAsync(seed));
        var sessionId = Guid.NewGuid();
        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>();
            var share = await context.EntryShares.SingleAsync(x => x.Id == seed.Session.ShareId, TestContext.Current.CancellationToken);
            context.Add(share.OpenSession(sessionId,
                scope.ServiceProvider.GetRequiredService<EntryShareSecurity>().HashSessionToken(sessionId, token),
                apiFactory.FakeClock.GetCurrentInstant(), Duration.FromMinutes(15)));
            await context.CommitAsync(TestContext.Current.CancellationToken);
        }

        var other = seed with { Session = seed.Session with { SessionId = sessionId, SessionToken = token } };

        // When
        var resend = await RequestAsync(other);
        var replay = await VerifyAsync(other, code);

        // Then
        resend.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        replay.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await VerifyAsync(seed, code)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task When_AttemptsAlternateBetweenPinAndOtp_Then_TheyStillExhaustOneBudget()
    {
        // Given
        var seed = await SeedAsync(pin: true);
        (await RequestAsync(seed)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var code = ReadCode(await ReadEmailAsync(seed));
        var limit = apiFactory.Services.GetRequiredService<IOptions<EntrySharingOptions>>().Value.FailedAttemptLimit;

        // When
        for (var index = 0; index < limit - 1; index++)
        {
            var response = await apiFactory.CreateClient().POSTAsync<VerifyEntryShareSecretEndpoint, VerifyEntryShareSecretRequest>(
                new VerifyEntryShareSecretRequest
                {
                    ShareId = seed.Session.ShareId, SessionId = seed.Session.SessionId,
                    SessionToken = seed.Session.SessionToken, Secret = "111111",
                });
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        (await VerifyAsync(seed, code == "000000" ? "111111" : "000000")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Then
        (await VerifyAsync(seed, code)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>().EntryShares
            .SingleAsync(x => x.Id == seed.Session.ShareId, TestContext.Current.CancellationToken)).FailedAttempts.ShouldBe(limit);
    }

    [Fact]
    public async Task When_ImmediateDispatchWasLost_Then_TheRecoveryJobPublishesPendingCode()
    {
        // Given
        var seed = await SeedAsync(pending: true);
        var publisher = Substitute.For<IBus>();
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>();
        var dispatcher = new EntryShareOtpDispatcher(context,
            scope.ServiceProvider.GetRequiredService<EntryShareAuthority>(), scope.ServiceProvider.GetRequiredService<EntryShareSecurity>(),
            publisher, apiFactory.FakeClock);
        var job = new DispatchEntryShareOtpJob(context, dispatcher,
            Options.Create(new DispatchEntryShareOtpJobOptions { BatchSize = 1 }));

        // When
        await job.ExecuteAsync(TestContext.Current.CancellationToken);

        // Then
        await publisher.Received(1).Publish(Arg.Is<SendEntryShareVerificationEmailCommand>(x => x.SessionId == seed.Session.SessionId),
            Arg.Any<IPipe<PublishContext<SendEntryShareVerificationEmailCommand>>>(), Arg.Any<CancellationToken>());
        (await context.EntryShareSessions.SingleAsync(x => x.Id == seed.Session.SessionId,
            TestContext.Current.CancellationToken)).ProtectedOtp.ShouldBeNull();
    }

    private Task<HttpResponseMessage> RequestAsync(OtpSeed seed, long generation = 1, string language = "en") =>
        apiFactory.CreateClient().POSTAsync<RequestEntryShareOtpEndpoint, RequestEntryShareOtpRequest>(
            new RequestEntryShareOtpRequest
            {
                ShareId = seed.Session.ShareId, SessionId = seed.Session.SessionId, SessionToken = seed.Session.SessionToken,
                Generation = generation, Language = language,
            });

    private Task<HttpResponseMessage> VerifyAsync(OtpSeed seed, string code, long generation = 1) =>
        apiFactory.CreateClient().POSTAsync<VerifyEntryShareOtpEndpoint, VerifyEntryShareOtpRequest>(
            new VerifyEntryShareOtpRequest
            {
                ShareId = seed.Session.ShareId, SessionId = seed.Session.SessionId, SessionToken = seed.Session.SessionToken,
                Generation = generation, Code = code,
            });

    private static Task<EmailMessage> ReadEmailAsync(OtpSeed seed) => seed.Emails.ReadAsync(TestContext.Current.CancellationToken)
        .AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    private static string ReadCode(EmailMessage email) => Regex.Match(email.TextBody!, @"(?<!\d)\d{6}(?!\d)").Value;

    private async Task<OtpSeed> SeedAsync(bool pin = false, bool pending = false)
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var entryId = await apiFactory.Services.SeedSyncEntryAsync(organization.Id, vault.Id, user.Id);
        var emails = Channel.CreateUnbounded<EmailMessage>();
        var recipient = $"recipient-{Guid.NewGuid():N}@example.test";
        apiFactory.EmailSender.SendAsync(Arg.Is<EmailMessage>(x => x.ToAddress == recipient), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                emails.Writer.TryWrite(call.Arg<EmailMessage>());
                return Task.CompletedTask;
            });
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<VaultDomainWriteContext>();
        var security = scope.ServiceProvider.GetRequiredService<EntryShareSecurity>();
        var sourceScope = new EntryScope(organization.Id, vault.Id, entryId);
        var source = await scope.ServiceProvider.GetRequiredService<EntryShareAuthority>()
            .LoadSenderSourceAsync(sourceScope, user.Id, 1, TestContext.Current.CancellationToken);
        var id = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var now = Instant.FromUnixTimeMilliseconds(apiFactory.FakeClock.GetCurrentInstant().ToUnixTimeMilliseconds());
        var protection = pin ? EntryShareProtection.Pin : EntryShareProtection.None;
        var share = EntryShare.Create(id, sourceScope, source.Entry.CurrentRevision, user.Id, now,
            now + Duration.FromHours(1), 1, EntryShareRecipientMode.NamedRecipient,
            security.ProtectRecipientEmail(id, recipient), protection,
            security.CreateSecretVerifier(id, protection, pin ? "493827" : null), new byte[32],
            RandomNumberGenerator.GetBytes(24), RandomNumberGenerator.GetBytes(64), false, 1, source.Member.AddedAt);
        var session = share.OpenSession(sessionId, security.HashSessionToken(sessionId, token), now, Duration.FromMinutes(15));
        context.Add(share);
        context.Add(session);
        await context.CommitAsync(TestContext.Current.CancellationToken);
        if (pending)
        {
            share.IssueOtp(session, security.HashOtp(sessionId, "493827"), now, Duration.FromMinutes(5), Duration.FromSeconds(60),
                new EntryShareOtpDelivery(1, security.ProtectOtp(id, sessionId, 1, "493827"), "en"));
            await scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>().SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        return new OtpSeed(new EntryShareSessionRequest { ShareId = id, SessionId = sessionId, SessionToken = token }, emails.Reader);
    }

    private sealed record OtpSeed(EntryShareSessionRequest Session, ChannelReader<EmailMessage> Emails);

    private sealed class EmailExpiryObserver(Guid sessionId, TaskCompletionSource<Instant> received) : IConsumeObserver
    {
        public Task PreConsume<T>(ConsumeContext<T> context) where T : class
        {
            if (context.Message is SendEntryShareVerificationEmailCommand message && message.SessionId == sessionId)
            {
                received.TrySetResult(message.ExpiresAt);
            }

            return Task.CompletedTask;
        }

        public Task PostConsume<T>(ConsumeContext<T> context) where T : class => Task.CompletedTask;
        public Task ConsumeFault<T>(ConsumeContext<T> context, Exception exception) where T : class => Task.CompletedTask;
    }
}
