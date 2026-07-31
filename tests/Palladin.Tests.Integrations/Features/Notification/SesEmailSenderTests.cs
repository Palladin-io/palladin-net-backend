using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using Palladin.Module.Notification.Infrastructure.Email;
using Palladin.Module.Notification.Infrastructure.Email.Suppression;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Notification;

public sealed class SesEmailSenderTests
{
    private readonly IAmazonSimpleEmailServiceV2 _client = Substitute.For<IAmazonSimpleEmailServiceV2>();
    private readonly IEmailSuppressionStore _suppression = Substitute.For<IEmailSuppressionStore>();

    private SesEmailSender CreateSender(SesOptions options) =>
        new(_client, _suppression, Options.Create(options), Substitute.For<ILogger<SesEmailSender>>());

    [Fact]
    public async Task When_Configured_Then_BuildsSesRequestFromMessageAndOptions()
    {
        // Given
        SendEmailRequest? captured = null;
        _client.SendEmailAsync(Arg.Do<SendEmailRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(new SendEmailResponse { MessageId = "message-id" });
        var sender = CreateSender(new SesOptions
        {
            FromAddress = "no-reply@palladin.io",
            FromName = "Palladin",
            ConfigurationSetName = "palladin-transactional",
        });

        // When
        await sender.SendAsync(
            new EmailMessage("user@example.com", "Verify your email", "<p>hello</p>", "hello"), CancellationToken.None);

        // Then
        captured.ShouldNotBeNull();
        captured.FromEmailAddress.ShouldBe("Palladin <no-reply@palladin.io>");
        captured.Destination.ToAddresses.ShouldBe(["user@example.com"]);
        captured.ConfigurationSetName.ShouldBe("palladin-transactional");
        captured.Content.Simple.Subject.Data.ShouldBe("Verify your email");
        captured.Content.Simple.Body.Html.Data.ShouldBe("<p>hello</p>");
        captured.Content.Simple.Body.Text.Data.ShouldBe("hello");
    }

    [Fact]
    public async Task When_TextBodyNull_Then_OmitsTextPart()
    {
        // Given
        SendEmailRequest? captured = null;
        _client.SendEmailAsync(Arg.Do<SendEmailRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(new SendEmailResponse { MessageId = "message-id" });
        var sender = CreateSender(new SesOptions { FromAddress = "no-reply@palladin.io", FromName = "Palladin" });

        // When
        await sender.SendAsync(
            new EmailMessage("user@example.com", "Subject", "<p>hello</p>"), CancellationToken.None);

        // Then
        captured.ShouldNotBeNull();
        captured.Content.Simple.Body.Text.ShouldBeNull();
        captured.ConfigurationSetName.ShouldBeNull();
    }

    [Fact]
    public async Task When_ClientNotConfigured_Then_NoOp()
    {
        // Given
        var sender = new SesEmailSender(
            null,
            _suppression,
            Options.Create(new SesOptions { FromAddress = "no-reply@palladin.io" }),
            Substitute.For<ILogger<SesEmailSender>>());

        // When
        await sender.SendAsync(new EmailMessage("user@example.com", "Subject", "<p>hi</p>"), CancellationToken.None);

        // Then
        await _client.DidNotReceiveWithAnyArgs().SendEmailAsync(default!, default);
    }

    [Fact]
    public async Task When_RecipientSuppressed_Then_DoesNotSend()
    {
        // Given
        _suppression.IsSuppressedAsync("user@example.com", Arg.Any<CancellationToken>()).Returns(true);
        var sender = CreateSender(new SesOptions { FromAddress = "no-reply@palladin.io" });

        // When
        await sender.SendAsync(new EmailMessage("user@example.com", "Subject", "<p>hi</p>"), CancellationToken.None);

        // Then
        await _client.DidNotReceiveWithAnyArgs().SendEmailAsync(default!, default);
    }
}
