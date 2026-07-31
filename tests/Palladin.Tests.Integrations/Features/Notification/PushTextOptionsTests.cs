using Palladin.Core.Types;
using Palladin.Module.Notification.Infrastructure.Push;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Notification;

public sealed class PushTextOptionsTests
{
    [Fact]
    public void When_TypeAndLanguageConfigured_Then_ReturnsThatCopy()
    {
        // Given
        var pushText = Build(new PushTextOptions
        {
            ByType =
            {
                ["grant_pending"] = new Dictionary<string, PushTextEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    ["en"] = new() { Title = "Grant title", Body = "Grant body" },
                },
            },
        });

        // When
        var (title, body) = pushText.For(NotificationType.GrantPending, "en");

        // Then
        title.ShouldBe("Grant title");
        body.ShouldBe("Grant body");
    }

    [Fact]
    public void When_TypeHasNoEntry_Then_FallsBackToDefault()
    {
        // Given
        var pushText = Build(new PushTextOptions
        {
            Default =
            {
                ["en"] = new() { Title = "Default title", Body = "Default body" },
            },
        });

        // When
        var (title, body) = pushText.For(NotificationType.CredentialStale, "en");

        // Then
        title.ShouldBe("Default title");
        body.ShouldBe("Default body");
    }

    [Fact]
    public void When_LanguageMissing_Then_FallsBackToDefaultLanguage()
    {
        // Given
        var pushText = Build(new PushTextOptions
        {
            ByType =
            {
                ["grant_pending"] = new Dictionary<string, PushTextEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    ["en"] = new() { Title = "EN title", Body = "EN body" },
                },
            },
        });

        // When
        var (title, body) = pushText.For(NotificationType.GrantPending, "pl");

        // Then
        title.ShouldBe("EN title");
        body.ShouldBe("EN body");
    }

    [Fact]
    public void When_NothingConfigured_Then_ReturnsEmptyCopy()
    {
        // Given
        var pushText = Build(new PushTextOptions());

        // When
        var (title, body) = pushText.For(NotificationType.GrantApproved, null);
        title.ShouldBeEmpty();
        body.ShouldBeEmpty();
    }

    [Fact]
    public void When_TemplateHasSensitivePlaceholders_Then_LockScreenCopyStaysGeneric()
    {
        // Given
        var pushText = Build(new PushTextOptions
        {
            ByType =
            {
                ["grant_pending"] = new Dictionary<string, PushTextEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    ["en"] = new()
                    {
                        Title = "Access request",
                        Body = "Agent {agentName} requests access to {entryLabel} in {vaultName}.",
                    },
                },
            },
        });

        // When
        var (title, body) = pushText.For(NotificationType.GrantPending, "en");

        // Then
        title.ShouldBe("Access request");
        body.ShouldNotContain("{");
        body.ShouldBe("Agent requests access to in.");
    }

    [Fact]
    public void When_TemplateContainsEntityPlaceholder_Then_NoEntityIdentityIsRendered()
    {
        // Given
        var pushText = Build(new PushTextOptions
        {
            ByType =
            {
                ["grant_revoked"] = new Dictionary<string, PushTextEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    ["en"] = new() { Title = "Access revoked", Body = "Access to {entryLabel} in {vaultName} was revoked." },
                },
            },
        });

        // When
        var (_, body) = pushText.For(NotificationType.GrantRevoked, "en");

        // Then
        body.ShouldNotContain("{");
        body.ShouldBe("Access to in was revoked.");
    }

    [Fact]
    public void When_NoMetadataOverload_Then_LeavesTemplateUntouched()
    {
        // Given
        var pushText = Build(new PushTextOptions
        {
            ByType =
            {
                ["grant_pending"] = new Dictionary<string, PushTextEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    ["en"] = new() { Title = "Access request", Body = "Agent {agentName} requests access." },
                },
            },
        });

        // When
        var (_, body) = pushText.For(NotificationType.GrantPending, "en");

        // Then
        body.ShouldBe("Agent requests access.");
    }

    private static PushText Build(PushTextOptions options) => new(Options.Create(options));
}
