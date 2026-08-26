using System.Reflection;
using System.Net;
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
        ["attemptCount"] = "4",
        ["windowMinutes"] = "5",
        ["ipAddresses"] = "198.51.100.10, 203.0.113.11",
        ["lockedUntil"] = "10 Jul 2026, 12:15 UTC",
        ["startsAtUtc"] = "2026-08-25T12:00:00Z",
        ["endsAtUtc"] = "2026-09-25T12:00:00Z",
    };

    private static readonly string[] AllTemplates = typeof(EmailTemplates)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.IsLiteral && !field.IsInitOnly && field.FieldType == typeof(string))
        .Select(field => (string)field.GetRawConstantValue()!)
        .OrderBy(name => name, StringComparer.Ordinal)
        .ToArray();

    private static readonly HashSet<string> TemplatesWithCallToAction =
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
        WebUtility.HtmlDecode(pl.HtmlBody).ShouldContain("Potwierdź swój e-mail");
        en.HtmlBody.ShouldContain("https://palladin.io/verify?token=abc");
        en.TextBody.ShouldContain("https://palladin.io/verify?token=abc");
    }

    [Fact]
    public void When_EmailVerificationRendered_Then_ProductLinksAreIncludedInHtmlAndPlainText()
    {
        // Given
        var renderer = new FluidEmailTemplateRenderer(
            Options.Create(new EmailBrandingOptions
            {
                AppName = "Example Vault",
                MobileAppUrl = "https://palladin.io/mobile",
                BrowserExtensionUrl = "https://palladin.io/browser-extension",
            }),
            new FakeClock(Instant.FromUtc(2026, 7, 10, 12, 0)));

        // When
        var english = renderer.Render(EmailTemplates.EmailVerification, "en", Model);
        var polish = renderer.Render(EmailTemplates.EmailVerification, "pl", Model);

        // Then
        english.HtmlBody.ShouldContain("href=\"https://palladin.io/mobile\"");
        english.HtmlBody.ShouldContain(">mobile app</a>");
        english.HtmlBody.ShouldContain("href=\"https://palladin.io/browser-extension\"");
        english.HtmlBody.ShouldContain(">browser extension</a>");
        english.TextBody.ShouldContain("Mobile app: https://palladin.io/mobile");
        english.TextBody.ShouldContain("Browser extension: https://palladin.io/browser-extension");
        english.TextBody.ShouldContain("Explore Example Vault beyond the web app:");

        polish.HtmlBody.ShouldContain(">aplikację mobilną</a>");
        polish.HtmlBody.ShouldContain(">rozszerzenie przeglądarkowe</a>");
        polish.TextBody.ShouldContain("Aplikacja mobilna: https://palladin.io/mobile");
        polish.TextBody.ShouldContain("Rozszerzenie przeglądarkowe: https://palladin.io/browser-extension");
        polish.TextBody.ShouldContain("Poznaj Example Vault poza panelem webowym:");
    }

    [Fact]
    public void When_OrganizationInvitationRendered_Then_DefaultPalladinHierarchyIsUsed()
    {
        // Given
        var renderer = CreateRenderer();

        // When
        var english = renderer.Render(EmailTemplates.OrganizationInvitation, "en", Model);
        var polish = renderer.Render(EmailTemplates.OrganizationInvitation, "pl", Model);

        // Then
        foreach (var rendered in new[] { english, polish })
        {
            rendered.HtmlBody.ShouldContain("margin:0 0 40px");
            rendered.HtmlBody.ShouldContain("margin:0 0 56px;text-align:center");
            rendered.HtmlBody.ShouldContain("padding:10px 20px");
            rendered.HtmlBody.ShouldContain("font-size:12px;line-height:1.55;color:#8a8a8a");
            rendered.HtmlBody.ShouldNotContain("font-size:11px");
            rendered.HtmlBody.IndexOf("text-align:center", StringComparison.Ordinal)
                .ShouldBeLessThan(rendered.HtmlBody.IndexOf("font-size:12px", StringComparison.Ordinal));
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
    public void When_LoginLockoutAlertRendered_Then_SecurityFactsAreShownWithoutActionLink()
    {
        var renderer = CreateRenderer();

        var english = renderer.Render(EmailTemplates.LoginLockoutAlert, "en", Model);
        var polish = renderer.Render(EmailTemplates.LoginLockoutAlert, "pl", Model);

        foreach (var rendered in new[] { english, polish })
        {
            rendered.HtmlBody.ShouldContain("198.51.100.10");
            rendered.HtmlBody.ShouldContain("203.0.113.11");
            rendered.HtmlBody.ShouldContain("10 Jul 2026, 12:15 UTC");
            rendered.HtmlBody.ShouldNotContain("padding:10px 20px");
            rendered.HtmlBody.ShouldNotContain(">Unlock account<");
            rendered.HtmlBody.ShouldNotContain(">Odblokuj konto<");
        }

        english.Subject.ShouldContain("login attempts blocked");
        english.HtmlBody.ShouldContain("4 failed attempts");
        polish.Subject.ShouldContain("zablokowane próby logowania");
        polish.HtmlBody.ShouldContain("4 nieudanych prób");
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
    public void When_WaitlistEmailsRendered_Then_DeveloperBenefitIsAccurateAndPremiumIsNotClaimed()
    {
        var renderer = CreateRenderer();

        var verificationEnglish = renderer.Render(EmailTemplates.WaitlistVerification, "en", Model);
        var verificationPolish = renderer.Render(EmailTemplates.WaitlistVerification, "pl", Model);
        var activatedEnglish = renderer.Render(EmailTemplates.WaitlistDeveloperBenefitActivated, "en", Model);
        var activatedPolish = renderer.Render(EmailTemplates.WaitlistDeveloperBenefitActivated, "pl", Model);

        foreach (var rendered in new[]
                 {
                     verificationEnglish,
                     verificationPolish,
                     activatedEnglish,
                     activatedPolish,
                 })
        {
            rendered.Subject.ShouldNotContain("Premium", Case.Insensitive);
            rendered.HtmlBody.ShouldNotContain("Premium", Case.Insensitive);
            rendered.TextBody.ShouldNotContain("Premium", Case.Insensitive);
            rendered.HtmlBody.ShouldContain("Developer");
            rendered.TextBody.ShouldContain("Developer");
        }

        foreach (var verification in new[] { verificationEnglish, verificationPolish })
        {
            verification.HtmlBody.ShouldContain("margin:0 0 40px");
            verification.HtmlBody.ShouldContain("margin:0 0 56px;text-align:center");
            verification.HtmlBody.ShouldContain("font-size:12px;line-height:1.55;color:#8a8a8a");
            verification.HtmlBody.ShouldContain("style=\"color:#8a8a8a;text-decoration:underline\"");
            verification.HtmlBody.ShouldContain("margin:0 0 4px");
        }

        verificationEnglish.HtmlBody.ShouldNotContain("requires no card");
        verificationEnglish.TextBody.ShouldNotContain("requires no card");
        verificationPolish.HtmlBody.ShouldNotContain("Benefit nie wymaga karty");
        verificationPolish.TextBody.ShouldNotContain("Benefit nie wymaga karty");

        activatedEnglish.TextBody.ShouldContain("2026-08-25T12:00:00Z");
        activatedEnglish.TextBody.ShouldContain("2026-09-25T12:00:00Z");
        activatedEnglish.TextBody.ShouldContain("No card");
        activatedEnglish.TextBody.ShouldContain("independently of this promotion");
        activatedPolish.TextBody.ShouldContain("2026-08-25T12:00:00Z");
        activatedPolish.TextBody.ShouldContain("2026-09-25T12:00:00Z");
        activatedPolish.TextBody.ShouldContain("karty");
        activatedPolish.TextBody.ShouldContain("niezależnie od tej promocji");
    }

    [Fact]
    public void When_EveryTemplateRenderedInBothLanguages_Then_DefaultPalladinLayoutAndAllPartsAreProduced()
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
                rendered.HtmlBody.ShouldContain("padding:32px;font-size:14px;line-height:1.6");
                rendered.HtmlBody.ShouldContain("margin:0 0 16px;font-size:20px;line-height:1.3;font-weight:700");
                rendered.HtmlBody.ShouldContain("margin:0 0 4px");
                rendered.HtmlBody.ShouldNotContain("font-size:15px;line-height:1.6");
                rendered.TextBody.ShouldNotBeNullOrWhiteSpace();

                if (TemplatesWithCallToAction.Contains(template))
                {
                    rendered.HtmlBody.ShouldContain("margin:0 0 56px;text-align:center");
                    rendered.HtmlBody.ShouldContain("padding:10px 20px;border-radius:8px;font-size:14px;line-height:1.4");
                }
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
        provider.GetFileInfo("_cta.html.liquid").Exists.ShouldBeTrue();
        provider.GetFileInfo("does-not-exist.liquid").Exists.ShouldBeFalse();
    }
}
