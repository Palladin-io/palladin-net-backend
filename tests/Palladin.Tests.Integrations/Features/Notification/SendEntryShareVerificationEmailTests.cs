using MassTransit;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Palladin.Module.Notification.Contracts.Commands;
using Palladin.Module.Notification.Contracts.ValueObjects;
using Palladin.Module.Notification.Features;
using Palladin.Module.Notification.Infrastructure.Email;
using Palladin.Module.Notification.Infrastructure.Email.Templating;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Notification;

public sealed class SendEntryShareVerificationEmailTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task When_TheSharingCodeCommandArrives_Then_OnlyAnUnexpiredCodeIsSentWithAStableIdempotencyKey(bool expired)
    {
        // Given
        var clock = new FakeClock(Instant.FromUtc(2026, 9, 20, 12, 0));
        var renderer = Substitute.For<IEmailTemplateRenderer>();
        renderer.Render(EmailTemplates.EntryShareVerification, "en", Arg.Any<IReadOnlyDictionary<string, object?>>())
            .Returns(new RenderedEmail("Code", "<p>Code</p>", "Code"));
        var deduplicator = Substitute.For<IEmailDispatchDeduplicator>();
        var command = new SendEntryShareVerificationEmailCommand(Guid.NewGuid(), Guid.NewGuid(), 2,
            "recipient@example.test", "493827", "en",
            clock.GetCurrentInstant() + Duration.FromMinutes(expired ? 0 : 5), clock.GetCurrentInstant() - Duration.FromSeconds(1));
        var context = Substitute.For<ConsumeContext<SendEntryShareVerificationEmailCommand>>();
        context.Message.Returns(command);

        // When
        await new SendEntryShareVerificationEmailConsumer(renderer, deduplicator, clock).Consume(context);

        // Then
        command.ToString().ShouldNotContain(command.Code);
        command.ToString().ShouldNotContain(command.Email);
        if (expired)
        {
            await deduplicator.DidNotReceive().SendOnceAsync(Arg.Any<string>(), Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
        }
        else
        {
            await deduplicator.Received(1).SendOnceAsync($"entry-share-otp:{command.ShareId:D}:{command.SessionId:D}:2",
                Arg.Is<EmailMessage>(x => x.ToAddress == command.Email), Arg.Any<CancellationToken>());
        }
    }
}
