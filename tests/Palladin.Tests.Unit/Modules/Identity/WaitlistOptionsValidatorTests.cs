using Microsoft.Extensions.Hosting;
using NSubstitute;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.Waitlist;

namespace Palladin.Tests.Unit.Modules.Identity;

public sealed class WaitlistOptionsValidatorTests
{
    [Fact]
    public void When_Disabled_Then_EmptyConfigurationSucceeds()
    {
        // Given
        var validator = new WaitlistOptionsValidator(Environment("Production"));

        // When
        var result = validator.Validate(null, new WaitlistOptions { Enabled = false });

        // Then
        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void When_Enabled_AndUrlsMissing_Then_Fails()
    {
        // Given
        var validator = new WaitlistOptionsValidator(Environment("Production"));

        // When
        var result = validator.Validate(null, new WaitlistOptions { Enabled = true });

        // Then
        result.Failed.ShouldBeTrue();
        result.Failures.Count().ShouldBe(5);
    }

    [Fact]
    public void When_Enabled_AndProductionUrlsUseHttp_Then_Fails()
    {
        // Given
        var validator = new WaitlistOptionsValidator(Environment("Production"));

        // When
        var result = validator.Validate(null, ValidOptions("http"));

        // Then
        result.Failed.ShouldBeTrue();
        result.Failures.Count().ShouldBe(5);
    }

    [Fact]
    public void When_Enabled_AndConfigurationValid_Then_Succeeds()
    {
        // Given
        var validator = new WaitlistOptionsValidator(Environment("Production"));

        // When
        var result = validator.Validate(null, ValidOptions("https"));

        // Then
        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void When_BenefitEnabledWithoutDates_Then_Fails()
    {
        // Given
        var validator = new WaitlistOptionsValidator(Environment("Production"));

        // When
        var result = validator.Validate(null, ValidOptions("https", benefitEnabled: true));

        // Then
        result.Failed.ShouldBeTrue();
        result.Failures.Count().ShouldBe(2);
    }

    [Fact]
    public void When_ClaimDeadlineIsNotExactlySixMonthsAfterLaunch_Then_Fails()
    {
        // Given
        var validator = new WaitlistOptionsValidator(Environment("Production"));
        var options = ValidOptions(
            "https",
            benefitEnabled: true,
            publicLaunch: DateTimeOffset.Parse("2026-09-01T00:00:00Z"),
            claimDeadline: DateTimeOffset.Parse("2027-02-28T00:00:00Z"));

        // When
        var result = validator.Validate(null, options);

        // Then
        result.Failed.ShouldBeTrue();
        result.Failures.ShouldContain(failure => failure.Contains("six calendar months"));
    }

    [Fact]
    public void When_BenefitDatesDefineSixCalendarMonths_Then_Succeeds()
    {
        // Given
        var validator = new WaitlistOptionsValidator(Environment("Production"));
        var options = ValidOptions(
            "https",
            benefitEnabled: true,
            publicLaunch: DateTimeOffset.Parse("2026-08-31T12:00:00Z"),
            claimDeadline: DateTimeOffset.Parse("2027-02-28T12:00:00Z"));

        // When
        var result = validator.Validate(null, options);

        // Then
        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void When_PromotionTermsVersionDoesNotMatchTheDeployedContract_Then_Fails()
    {
        // Given
        var validator = new WaitlistOptionsValidator(Environment("Production"));
        var options = ValidOptions(
            "https",
            benefitEnabled: true,
            publicLaunch: DateTimeOffset.Parse("2026-08-31T12:00:00Z"),
            claimDeadline: DateTimeOffset.Parse("2027-02-28T12:00:00Z"),
            promotionTermsVersion: "legacy-v0");

        // When
        var result = validator.Validate(null, options);

        // Then
        result.Failed.ShouldBeTrue();
        result.Failures.ShouldContain(failure => failure.Contains("PromotionTermsVersion"));
    }

    private static WaitlistOptions ValidOptions(
        string scheme,
        bool benefitEnabled = false,
        DateTimeOffset? publicLaunch = null,
        DateTimeOffset? claimDeadline = null,
        string promotionTermsVersion = WaitlistEntry.CurrentPromotionTermsVersion) => new()
    {
        Enabled = true,
        BenefitEnabled = benefitEnabled,
        PublicLaunchAtUtc = publicLaunch,
        BenefitClaimDeadlineAtUtc = claimDeadline,
        PromotionTermsVersion = promotionTermsVersion,
        VerificationUrlBase = $"{scheme}://api.palladin.io/api/waitlist/verify",
        VerifiedRedirectUrl = $"{scheme}://palladin.io/waitlist/confirmed",
        AlreadyVerifiedRedirectUrl = $"{scheme}://palladin.io/waitlist/already-confirmed",
        InvalidRedirectUrl = $"{scheme}://palladin.io/waitlist/invalid",
        TemporaryFailureRedirectUrl = $"{scheme}://palladin.io/waitlist/unavailable",
    };

    private static IHostEnvironment Environment(string name)
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(name);
        return environment;
    }
}
