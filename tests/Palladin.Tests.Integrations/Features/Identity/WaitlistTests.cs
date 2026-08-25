using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Features;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Infrastructure.Waitlist;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Seeders;
using FastEndpoints;
using Microsoft.AspNetCore.Http;
using FastEndpoints.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NSubstitute;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Identity;

[Collection<ApiFactoryCollection>]
public sealed class WaitlistTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_BothOptInsVerifyConcurrently_Then_SharedFenceActivatesBenefitOnce()
    {
        // Given — account and waitlist start unverified, with the eligible entry created first.
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = apiFactory.FakeClock.GetCurrentInstant();
        var email = $"waitlist-race-{Guid.NewGuid():N}@example.com";
        var entryId = await SeedEntryAsync(
            $"token-{Guid.NewGuid():N}",
            now - Duration.FromHours(1),
            email);
        var (user, _) = await apiFactory.Services.SeedPasswordUserAsync(
            new byte[32],
            email: email,
            emailVerified: false);

        await using var accountScope = apiFactory.Services.CreateAsyncScope();
        var accountContext = accountScope.ServiceProvider.GetRequiredService<IdentityDomainWriteContext>();
        var accountActivator = accountScope.ServiceProvider
            .GetRequiredService<WaitlistDeveloperBenefitActivator>();
        await using var accountTransaction = await accountContext.BeginTransactionAsync(cancellationToken);
        var accountUser = await accountContext.Users.SingleAsync(
            candidate => candidate.Id == user.Id,
            cancellationToken);
        accountUser.MarkEmailVerified(now);
        (await accountActivator.TryActivateAsync(accountUser, now, cancellationToken)).ShouldBeNull();

        var contenderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitlistVerification = Task.Run(async () =>
        {
            await using var waitlistScope = apiFactory.Services.CreateAsyncScope();
            var waitlistContext = waitlistScope.ServiceProvider
                .GetRequiredService<IdentityDomainWriteContext>();
            var waitlistActivator = waitlistScope.ServiceProvider
                .GetRequiredService<WaitlistDeveloperBenefitActivator>();
            await using var waitlistTransaction = await waitlistContext.BeginTransactionAsync(cancellationToken);
            var waitlistEntry = await waitlistContext.WaitlistEntries.SingleAsync(
                candidate => candidate.Id == entryId,
                cancellationToken);
            waitlistEntry.Verify(now);
            contenderStarted.SetResult();
            await waitlistActivator.TryActivateAsync(waitlistEntry, now, cancellationToken);
            await waitlistContext.CommitAsync(waitlistTransaction, cancellationToken);
        }, cancellationToken);

        await contenderStarted.Task.WaitAsync(cancellationToken);
        var completedBeforeLockReleased = await Task.WhenAny(
            waitlistVerification,
            Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken)) == waitlistVerification;

        await accountContext.CommitAsync(accountTransaction, cancellationToken);
        await waitlistVerification;

        // Then — the second verifier waited for the shared row, re-read the committed account state,
        // and performed the only claim instead of leaving two verified records without a benefit.
        completedBeforeLockReleased.ShouldBeFalse();
        await using var verificationScope = apiFactory.Services.CreateAsyncScope();
        var readContext = verificationScope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        var persistedEntry = await readContext.WaitlistEntries.SingleAsync(
            candidate => candidate.Id == entryId,
            cancellationToken);
        var persistedUser = await readContext.Users.SingleAsync(
            candidate => candidate.Id == user.Id,
            cancellationToken);
        persistedEntry.VerifiedAt.ShouldNotBeNull();
        persistedEntry.DeveloperBenefitUserId.ShouldBe(user.Id);
        persistedUser.EmailVerified.ShouldBeTrue();
        persistedUser.WaitlistDeveloperBenefitStartedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task When_JoiningWaitlist_Then_PendingEntryCreated()
    {
        // Given
        var email = $"waitlist-{Guid.NewGuid():N}@example.com";
        apiFactory.GuidProvider.Generate().Returns(_ => Guid.NewGuid());
        var client = apiFactory.CreateClient();

        // When
        var (response, result) = await client.POSTAsync<JoinWaitlistEndpoint, JoinWaitlistRequest, JoinWaitlistResponse>(
            new JoinWaitlistRequest { Email = $"  {email.ToUpperInvariant()}  ", Language = "pl" });

        // Then
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Accepted);
        result.Status.ShouldBe("accepted");

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var entry = await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>()
            .WaitlistEntries.SingleAsync(x => x.Email == email, TestContext.Current.CancellationToken);
        entry.VerifiedAt.ShouldBeNull();
        entry.Language.ShouldBe("pl");
        entry.TokenHash.ShouldNotBeNullOrWhiteSpace();
        entry.TokenExpiresAt.ShouldBeGreaterThan(entry.TokenIssuedAt);
    }

    [Fact]
    public async Task When_JoiningAgainWithinCooldown_Then_TokenUnchanged()
    {
        // Given
        var email = $"waitlist-{Guid.NewGuid():N}@example.com";
        apiFactory.GuidProvider.Generate().Returns(_ => Guid.NewGuid());
        var client = apiFactory.CreateClient();
        await client.POSTAsync<JoinWaitlistEndpoint, JoinWaitlistRequest, JoinWaitlistResponse>(
            new JoinWaitlistRequest { Email = email });
        var firstHash = await ReadTokenHashAsync(email);

        // When
        var (response, _) = await client.POSTAsync<JoinWaitlistEndpoint, JoinWaitlistRequest, JoinWaitlistResponse>(
            new JoinWaitlistRequest { Email = email });

        // Then
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Accepted);
        (await ReadTokenHashAsync(email)).ShouldBe(firstHash);
    }

    [Fact]
    public async Task When_VerifyingWithValidToken_Then_EntryVerifiedAndRedirected()
    {
        // Given
        var token = $"token-{Guid.NewGuid():N}";
        var entryId = await SeedEntryAsync(token, apiFactory.FakeClock.GetCurrentInstant());
        var client = apiFactory.CreateClient(new ClientOptions { AllowAutoRedirect = false });

        // When
        var response = await client.GetAsync($"api/waitlist/verify?token={token}", TestContext.Current.CancellationToken);

        // Then
        ((int)response.StatusCode).ShouldBe(StatusCodes.Status302Found);
        response.Headers.Location!.ToString().ShouldBe("https://palladin.io/waitlist/confirmed");

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var entry = await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>()
            .WaitlistEntries.SingleAsync(x => x.Id == entryId, TestContext.Current.CancellationToken);
        entry.VerifiedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task When_VerifyingWaitlistAfterVerifiedAccountCreation_Then_DeveloperBenefitIsActivated()
    {
        // Given — the waitlist entry existed before the account, but the account was verified first.
        var token = $"token-{Guid.NewGuid():N}";
        var email = $"waitlist-order-{Guid.NewGuid():N}@example.com";
        var entryId = await SeedEntryAsync(
            token,
            apiFactory.FakeClock.GetCurrentInstant() - Duration.FromHours(1),
            email);
        var (user, _) = await apiFactory.Services.SeedPasswordUserAsync(
            new byte[32],
            email: email,
            emailVerified: true);
        var expectedEndsAt = apiFactory.FakeClock.GetCurrentInstant()
            .InUtc()
            .LocalDateTime
            .PlusMonths(1)
            .InUtc()
            .ToInstant();
        var client = apiFactory.CreateClient(new ClientOptions { AllowAutoRedirect = false });

        // When
        var response = await client.GetAsync(
            $"api/waitlist/verify?token={token}",
            TestContext.Current.CancellationToken);

        // Then
        ((int)response.StatusCode).ShouldBe(StatusCodes.Status302Found);

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        var entry = await readContext.WaitlistEntries.SingleAsync(
            candidate => candidate.Id == entryId,
            TestContext.Current.CancellationToken);
        var persistedUser = await readContext.Users.SingleAsync(
            candidate => candidate.Id == user.Id,
            TestContext.Current.CancellationToken);
        entry.DeveloperBenefitUserId.ShouldBe(user.Id);
        entry.DeveloperBenefitEndsAt.ShouldBe(TruncateToMicroseconds(expectedEndsAt));
        persistedUser.WaitlistDeveloperBenefitEndsAt.ShouldBe(TruncateToMicroseconds(expectedEndsAt));
    }

    [Fact]
    public async Task When_VerifyingWithUnknownToken_Then_RedirectedToFailure()
    {
        // Given
        var client = apiFactory.CreateClient(new ClientOptions { AllowAutoRedirect = false });

        // When
        var response = await client.GetAsync(
            $"api/waitlist/verify?token=unknown-{Guid.NewGuid():N}", TestContext.Current.CancellationToken);

        // Then
        ((int)response.StatusCode).ShouldBe(StatusCodes.Status302Found);
        response.Headers.Location!.ToString().ShouldBe("https://palladin.io/waitlist/invalid");
    }

    [Fact]
    public async Task When_VerifyingWithExpiredToken_Then_RedirectedToFailure()
    {
        // Given — entry issued 48h ago with a 24h TTL
        var token = $"token-{Guid.NewGuid():N}";
        var issuedAt = apiFactory.FakeClock.GetCurrentInstant() - Duration.FromHours(48);
        await SeedEntryAsync(token, issuedAt);
        var client = apiFactory.CreateClient(new ClientOptions { AllowAutoRedirect = false });

        // When
        var response = await client.GetAsync($"api/waitlist/verify?token={token}", TestContext.Current.CancellationToken);

        // Then
        ((int)response.StatusCode).ShouldBe(StatusCodes.Status302Found);
        response.Headers.Location!.ToString().ShouldBe("https://palladin.io/waitlist/invalid");
    }

    private async Task<Guid> SeedEntryAsync(string token, Instant issuedAt, string? email = null)
    {
        var entry = WaitlistEntry.Join(
            Guid.NewGuid(), email ?? $"seed-{Guid.NewGuid():N}@example.com", "en",
            token, TokenService.HashToken(token), Duration.FromHours(24), issuedAt);

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
        writeContext.WaitlistEntries.Add(entry);
        await writeContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        return entry.Id;
    }

    private async Task<string> ReadTokenHashAsync(string email)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>()
            .WaitlistEntries.Where(x => x.Email == email)
            .Select(x => x.TokenHash)
            .SingleAsync(TestContext.Current.CancellationToken);
    }

    private static Instant TruncateToMicroseconds(Instant value)
    {
        var ticks = value.ToUnixTimeTicks();
        return Instant.FromUnixTimeTicks(ticks - ticks % 10);
    }
}
