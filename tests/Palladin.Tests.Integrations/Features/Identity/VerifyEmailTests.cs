using System.Net;
using System.Net.Http.Json;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Features;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Seeders;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Identity;

[Collection<ApiFactoryCollection>]
public sealed class VerifyEmailTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_ValidToken_Then_MarksVerifiedAndConsumesToken()
    {
        // Given
        var email = $"verify-{Guid.NewGuid():N}@example.com";
        var (user, _) = await apiFactory.Services.SeedPasswordUserAsync(new byte[32], email: email);
        var token = await SeedTokenAsync(user.Id, email, apiFactory.FakeClock.GetCurrentInstant(), Duration.FromMinutes(60));
        var client = apiFactory.CreateClient();

        // When
        var (response, result) = await client.POSTAsync<VerifyEmailEndpoint, VerifyEmailRequest, VerifyEmailResponse>(
            new VerifyEmailRequest { Token = token });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.Status.ShouldBe("verified");
        result.UserId.ShouldBe(user.Id);
        result.WaitlistDeveloperBenefitEndsAt.ShouldBeNull();

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        (await readContext.Users.FirstAsync(u => u.Id == user.Id, TestContext.Current.CancellationToken))
            .EmailVerified.ShouldBeTrue();
    }

    [Fact]
    public async Task When_VerifiedWaitlistUserVerifiesAccountEmail_Then_DeveloperBenefitIsActivated()
    {
        // Given — joining and confirming the waitlist happened before account creation.
        var email = $"waitlist-account-{Guid.NewGuid():N}@example.com";
        var waitlistJoinedAt = apiFactory.FakeClock.GetCurrentInstant() - Duration.FromDays(1);
        await SeedVerifiedWaitlistEntryAsync(email, waitlistJoinedAt);
        var (user, _) = await apiFactory.Services.SeedPasswordUserAsync(new byte[32], email: email);
        var token = await SeedTokenAsync(
            user.Id,
            email,
            apiFactory.FakeClock.GetCurrentInstant(),
            Duration.FromMinutes(60));
        var expectedEndsAt = apiFactory.FakeClock.GetCurrentInstant()
            .InUtc()
            .LocalDateTime
            .PlusMonths(1)
            .InUtc()
            .ToInstant();

        // When
        var (response, result) = await apiFactory.CreateClient()
            .POSTAsync<VerifyEmailEndpoint, VerifyEmailRequest, VerifyEmailResponse>(
                new VerifyEmailRequest { Token = token });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.UserId.ShouldBe(user.Id);
        result.WaitlistDeveloperBenefitEndsAt.ShouldBe(expectedEndsAt);

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        var persistedUser = await readContext.Users.SingleAsync(
            candidate => candidate.Id == user.Id,
            TestContext.Current.CancellationToken);
        var persistedEntry = await readContext.WaitlistEntries.SingleAsync(
            candidate => candidate.Email == email,
            TestContext.Current.CancellationToken);
        persistedUser.EmailVerified.ShouldBeTrue();
        persistedUser.WaitlistDeveloperBenefitStartedAt.ShouldBe(
            TruncateToMicroseconds(apiFactory.FakeClock.GetCurrentInstant()));
        persistedUser.WaitlistDeveloperBenefitEndsAt.ShouldBe(TruncateToMicroseconds(expectedEndsAt));
        persistedEntry.DeveloperBenefitUserId.ShouldBe(user.Id);
        persistedEntry.DeveloperBenefitStartedAt.ShouldBe(
            TruncateToMicroseconds(apiFactory.FakeClock.GetCurrentInstant()));
        persistedEntry.DeveloperBenefitEndsAt.ShouldBe(TruncateToMicroseconds(expectedEndsAt));
    }

    [Fact]
    public async Task When_TokenReused_Then_SecondUseIsInvalid()
    {
        // Given
        var email = $"reuse-{Guid.NewGuid():N}@example.com";
        var (user, _) = await apiFactory.Services.SeedPasswordUserAsync(new byte[32], email: email);
        var token = await SeedTokenAsync(user.Id, email, apiFactory.FakeClock.GetCurrentInstant(), Duration.FromMinutes(60));
        var client = apiFactory.CreateClient();
        await client.POSTAsync<VerifyEmailEndpoint, VerifyEmailRequest, VerifyEmailResponse>(
            new VerifyEmailRequest { Token = token });

        // When
        var response = await client.PostAsJsonAsync("api/auth/verify-email", new { Token = token }, TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldContain("verification-token-invalid");
    }

    [Fact]
    public async Task When_ExpiredToken_Then_ReturnsExpiredKey()
    {
        // Given — issued 2h ago with a 1h TTL
        var email = $"expired-{Guid.NewGuid():N}@example.com";
        var (user, _) = await apiFactory.Services.SeedPasswordUserAsync(new byte[32], email: email);
        var issuedAt = apiFactory.FakeClock.GetCurrentInstant() - Duration.FromHours(2);
        var token = await SeedTokenAsync(user.Id, email, issuedAt, Duration.FromHours(1));
        var client = apiFactory.CreateClient();

        // When
        var response = await client.PostAsJsonAsync("api/auth/verify-email", new { Token = token }, TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldContain("verification-token-expired");
    }

    private async Task<string> SeedTokenAsync(Guid userId, string email, Instant issuedAt, Duration ttl)
    {
        var token = $"tok-{Guid.NewGuid():N}";
        var entity = VerificationToken.CreateEmailVerification(
            Guid.NewGuid(), userId, email, "en", token, TokenService.HashToken(token), ttl, issuedAt);

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
        writeContext.VerificationTokens.Add(entity);
        await writeContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        return token;
    }

    private async Task SeedVerifiedWaitlistEntryAsync(string email, Instant joinedAt)
    {
        var entry = WaitlistEntry.Join(
            Guid.NewGuid(),
            email,
            "pl",
            "waitlist-token",
            TokenService.HashToken($"waitlist-{Guid.NewGuid():N}"),
            Duration.FromDays(2),
            joinedAt);
        entry.Verify(joinedAt + Duration.FromMinutes(1));

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
        writeContext.WaitlistEntries.Add(entry);
        await writeContext.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static Instant TruncateToMicroseconds(Instant value)
    {
        var ticks = value.ToUnixTimeTicks();
        return Instant.FromUnixTimeTicks(ticks - ticks % 10);
    }
}
