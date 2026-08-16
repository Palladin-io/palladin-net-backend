using Palladin.Module.Notification.Contracts.Commands;
using Palladin.Module.Notification.Contracts.ValueObjects;
using Palladin.Module.Notification.Features;
using Palladin.Module.Notification.Infrastructure.Email;
using Palladin.Module.Notification.Infrastructure.Email.Templating;
using Palladin.Module.Notification.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NSubstitute;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Notification;

public sealed class SendEmailConsumerTests
{
    [Fact]
    public async Task When_CommandConsumed_Then_RendersTemplateAndSends()
    {
        // Given
        var renderer = Substitute.For<IEmailTemplateRenderer>();
        renderer.Render(EmailTemplates.WaitlistVerification, "pl", Arg.Any<IReadOnlyDictionary<string, object?>>())
            .Returns(new RenderedEmail("Temat", "<p>html</p>", "tekst"));
        var dispatchDeduplicator = Substitute.For<IEmailDispatchDeduplicator>();

        var command = new SendEmailCommand(
            "user@example.com",
            EmailTemplates.WaitlistVerification,
            "pl",
            new Dictionary<string, string> { ["verificationUrl"] = "https://api.palladin.io/api/waitlist/verify?token=t" },
            Instant.FromUtc(2026, 7, 11, 12, 0));
        var context = Substitute.For<ConsumeContext<SendEmailCommand>>();
        context.Message.Returns(command);

        // When
        await new SendEmailConsumer(renderer, dispatchDeduplicator).Consume(context);

        // Then
        await dispatchDeduplicator.Received(1).SendOnceAsync(
            null,
            Arg.Is<EmailMessage>(m =>
                m.ToAddress == "user@example.com"
                && m.Subject == "Temat"
                && m.HtmlBody == "<p>html</p>"
                && m.TextBody == "tekst"),
            Arg.Any<CancellationToken>());
        renderer.Received(1).Render(
            EmailTemplates.WaitlistVerification, "pl",
            Arg.Is<IReadOnlyDictionary<string, object?>>(m => (string?)m["verificationUrl"] != null));
    }
}

[Collection<ApiFactoryCollection>]
public sealed class EmailDispatchDeduplicatorTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_IdempotencyKeyIsConsumedTwice_Then_EmailIsSentOnce()
    {
        // Given
        var sender = Substitute.For<IEmailSender>();
        var message = new EmailMessage("user@example.com", "Subject", "<p>Body</p>", "Body");
        var idempotencyKey = $"organization-invitation:{Guid.NewGuid()}";

        // When
        await SendAsync(idempotencyKey, sender, message);
        await SendAsync(idempotencyKey, sender, message);

        // Then
        await sender.Received(1).SendAsync(message, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task When_ProviderFails_Then_DurableClaimIsReleasedAndRetryCanSend()
    {
        var sender = Substitute.For<IEmailSender>();
        var message = new EmailMessage("user@example.com", "Subject", "<p>Body</p>", "Body");
        var idempotencyKey = $"organization-invitation:{Guid.NewGuid()}";
        var attempt = 0;
        sender.SendAsync(message, Arg.Any<CancellationToken>()).Returns(_ =>
            Interlocked.Increment(ref attempt) == 1
                ? Task.FromException(new InvalidOperationException("provider unavailable"))
                : Task.CompletedTask);

        await Should.ThrowAsync<InvalidOperationException>(() => SendAsync(idempotencyKey, sender, message));
        await SendAsync(idempotencyKey, sender, message);
        await SendAsync(idempotencyKey, sender, message);

        await sender.Received(2).SendAsync(message, Arg.Any<CancellationToken>());
    }

    private async Task SendAsync(string idempotencyKey, IEmailSender sender, EmailMessage message)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var deduplicator = new EmailDispatchDeduplicator(
            scope.ServiceProvider.GetRequiredService<NotificationDomainWriteContext>(),
            sender,
            scope.ServiceProvider.GetRequiredService<IClock>(),
            scope.ServiceProvider.GetRequiredService<Palladin.Core.Guid.IGuidProvider>());

        await deduplicator.SendOnceAsync(idempotencyKey, message);
    }
}
