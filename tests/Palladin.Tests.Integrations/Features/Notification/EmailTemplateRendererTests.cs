using System.Reflection;
using Palladin.Module.Notification.Contracts.Commands;
using Palladin.Module.Notification.Contracts.ValueObjects;
using Palladin.Module.Notification.Infrastructure.Email.Templating;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Notification;

public sealed class EmailTemplateRendererTests
{
    private static FluidEmailTemplateRenderer CreateRenderer(string? logoUrl = null) =>
        new(
            Options.Create(new EmailBrandingOptions { LogoUrl = logoUrl }),
            new FakeClock(Instant.FromUtc(2026, 7, 10, 12, 0)));

    private static readonly Dictionary<string, object?> Model = new()
    {
        ["name"] = "Ada",
        ["verificationUrl"] = "https://palladin.io/verify?token=abc",
        ["expiryMinutes"] = 60,
        ["expiryHours"] = 24,
        ["invitationUrl"] = "https://palladin.io/beta?token=xyz",
        ["organizationName"] = "Ada's Organization",
        ["invitedByName"] = "Grace Hopper",
        ["role"] = "User",
        ["optOutUrl"] = "https://palladin.io/unsubscribe?token=xyz",
        ["eventTitle"] = "New sign-in",
        ["eventDescription"] = "A new device signed in.",
        ["occurredAt"] = "10 Jul 2026, 12:00 UTC",
    };

    private static readonly string[] AllTemplates =
    [
        EmailTemplates.EmailVerification,
        EmailTemplates.BetaInvitation,
        EmailTemplates.OrganizationInvitation,
        EmailTemplates.SecurityAlert,
        EmailTemplates.WaitlistVerification,
    ];

    [Fact]
    public void When_RenderedInBothLanguages_Then_SubjectAndBodyAreLocalized()
    {
        // Given
        var renderer = CreateRenderer();

        // When
        var en = renderer.Render(EmailTemplates.EmailVerification, "en", Model);
        var pl = renderer.Render(EmailTemplates.EmailVerification, "pl", Model);

        // Then
        en.Subject.ShouldContain("Verify");
        pl.Subject.ShouldContain("Potwierdź");
        en.HtmlBody.ShouldContain("Confirm your email");
        pl.HtmlBody.ShouldContain("Potwierdź swój e-mail");
        en.HtmlBody.ShouldContain("https://palladin.io/verify?token=abc");
        en.TextBody.ShouldContain("https://palladin.io/verify?token=abc");
    }

    [Fact]
    public void When_OrganizationInvitationRendered_Then_RightAlignedCallToActionPrecedesCompactNote()
    {
        // Given
        var renderer = CreateRenderer();

        // When
        var english = renderer.Render(EmailTemplates.OrganizationInvitation, "en", Model);
        var polish = renderer.Render(EmailTemplates.OrganizationInvitation, "pl", Model);

        // Then
        foreach (var rendered in new[] { english, polish })
        {
            rendered.HtmlBody.ShouldContain("font-size:11px;line-height:1.45");
            rendered.HtmlBody.ShouldContain("margin:0 0 16px;text-align:right");
            rendered.HtmlBody.ShouldContain("padding:10px 20px");
            rendered.HtmlBody.ShouldContain("font-size:14px");
            rendered.HtmlBody.ShouldNotContain("margin:0 0 16px;font-size:13px");
            rendered.HtmlBody.IndexOf("text-align:right", StringComparison.Ordinal)
                .ShouldBeLessThan(rendered.HtmlBody.IndexOf("font-size:11px", StringComparison.Ordinal));
        }

        english.TextBody.ShouldContain("before accepting. If you were not expecting");
        polish.TextBody.ShouldContain("adresu e-mail. Jeśli nie oczekujesz");
    }

    [Fact]
    public void When_ModelContainsHtml_Then_HtmlBodyEscapesIt()
    {
        // Given
        var renderer = CreateRenderer();
        var model = new Dictionary<string, object?>(Model)
        {
            ["eventTitle"] = "Suspicious login",
            ["eventDescription"] = "<script>alert('xss')</script>",
        };

        // When
        var rendered = renderer.Render(EmailTemplates.SecurityAlert, "en", model);

        // Then
        rendered.HtmlBody.ShouldContain("&lt;script&gt;");
        rendered.HtmlBody.ShouldNotContain("<script>alert");
    }

    [Fact]
    public void When_LogoConfigured_Then_HeaderUsesNavbarSizedDecorativeImage()
    {
        // Given
        var renderer = CreateRenderer("https://palladin.io/logo.png");

        // When
        var rendered = renderer.Render(EmailTemplates.EmailVerification, "en", Model);

        // Then
        rendered.HtmlBody.ShouldContain("src=\"https://palladin.io/logo.png\"");
        rendered.HtmlBody.ShouldContain("alt=\"\" role=\"presentation\" width=\"26\" height=\"26\"");
        rendered.HtmlBody.ShouldContain("font-size:17px;font-weight:800");
    }

    [Fact]
    public void When_ModelValueContainsNewline_Then_SubjectStaysSingleLine()
    {
        // Given
        var renderer = CreateRenderer();
        var model = new Dictionary<string, object?>(Model) { ["eventTitle"] = "Line one\nLine two\r\nLine three" };

        // When
        var rendered = renderer.Render(EmailTemplates.SecurityAlert, "en", model);

        // Then
        rendered.Subject.ShouldNotContain("\n");
        rendered.Subject.ShouldNotContain("\r");
        rendered.Subject.ShouldContain("Line one Line two Line three");
    }

    [Fact]
    public void When_LanguageUnsupported_Then_FallsBackToEnglish()
    {
        // Given
        var renderer = CreateRenderer();

        // When
        var fallback = renderer.Render(EmailTemplates.EmailVerification, "de", Model);
        var english = renderer.Render(EmailTemplates.EmailVerification, "en", Model);

        // Then
        fallback.Subject.ShouldBe(english.Subject);
        fallback.HtmlBody.ShouldBe(english.HtmlBody);
    }

    [Fact]
    public void When_EveryTemplateRenderedInBothLanguages_Then_AllPartsProduced()
    {
        // Given
        var renderer = CreateRenderer();

        // When / Then
        foreach (var template in AllTemplates)
        {
            foreach (var language in new[] { "en", "pl" })
            {
                var rendered = renderer.Render(template, language, Model);
                rendered.Subject.ShouldNotBeNullOrWhiteSpace();
                rendered.HtmlBody.ShouldContain("<html>");
                rendered.HtmlBody.ShouldContain("Palladin");
                rendered.TextBody.ShouldNotBeNullOrWhiteSpace();
            }
        }
    }

    [Fact]
    public void When_TemplateResourceMissing_Then_FileProviderReportsNotFound()
    {
        // Given
        var assembly = typeof(FluidEmailTemplateRenderer).Assembly;
        var prefix = typeof(FluidEmailTemplateRenderer).Namespace + ".Templates.";
        var provider = new EmbeddedTemplateFileProvider(assembly, prefix);

        // When / Then
        provider.GetFileInfo("_header.html.liquid").Exists.ShouldBeTrue();
        provider.GetFileInfo("does-not-exist.liquid").Exists.ShouldBeFalse();
    }
}
