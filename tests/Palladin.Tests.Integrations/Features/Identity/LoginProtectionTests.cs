using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NodaTime;
using NSubstitute;
using Palladin.Module.Identity.Features;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.Login;
using Palladin.Module.Identity.Infrastructure.PasswordAuth;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Identity;

[Collection<ApiFactoryCollection>]
public sealed class LoginProtectionTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_ConcurrentFailuresCreateCounter_Then_NoAttemptIsLost()
    {
        apiFactory.GuidProvider.Generate().Returns(_ => Guid.NewGuid());
        var email = $"concurrent-new-{Guid.NewGuid():N}@example.com";
        var ip = $"198.51.100.{Random.Shared.Next(1, 255)}";
        var now = apiFactory.FakeClock.GetCurrentInstant();
        var services = CreateThrottleServices(5);

        var results = await Task.WhenAll(services.Select(service =>
            service.RecordFailureAsync(email, ip, now, TestContext.Current.CancellationToken)));

        results.Count(result => result.IsLocked).ShouldBe(1);
        var lockout = await ReadLockoutAsync(email, ip);
        lockout.Version.ShouldBe(5u);
        lockout.FailedCount.ShouldBe(0);
        lockout.LockedUntil.ShouldBe(now + Duration.FromMinutes(15));
    }

    [Fact]
    public async Task When_ConcurrentFailuresUpdateExistingCounter_Then_NoAttemptIsLost()
    {
        apiFactory.GuidProvider.Generate().Returns(_ => Guid.NewGuid());
        var email = $"concurrent-existing-{Guid.NewGuid():N}@example.com";
        var ip = $"203.0.113.{Random.Shared.Next(1, 255)}";
        var now = apiFactory.FakeClock.GetCurrentInstant();
        await SeedEmptyLockoutAsync(email, ip, now);
        var services = CreateThrottleServices(5);

        var results = await Task.WhenAll(services.Select(service =>
            service.RecordFailureAsync(email, ip, now, TestContext.Current.CancellationToken)));

        results.Count(result => result.IsLocked).ShouldBe(1);
        var lockout = await ReadLockoutAsync(email, ip);
        lockout.Version.ShouldBe(5u);
        lockout.FailedCount.ShouldBe(0);
        lockout.LockedUntil.ShouldBe(now + Duration.FromMinutes(15));
    }

    [Fact]
    public async Task When_ResetRacesWithFailure_Then_BothOperationsAreLinearized()
    {
        apiFactory.GuidProvider.Generate().Returns(_ => Guid.NewGuid());
        var email = $"concurrent-reset-{Guid.NewGuid():N}@example.com";
        var ip = $"192.0.2.{Random.Shared.Next(1, 255)}";
        var now = apiFactory.FakeClock.GetCurrentInstant();
        var services = CreateThrottleServices(5);
        await services[0].RecordFailureAsync(email, ip, now, TestContext.Current.CancellationToken);

        await using var resetScope = apiFactory.Services.CreateAsyncScope();
        var resetContext = resetScope.ServiceProvider.GetRequiredService<IdentityDomainWriteContext>();
        var stagedReset = await services[1].StageResetAsync(
            resetContext,
            email,
            ip,
            now + Duration.FromSeconds(1),
            TestContext.Current.CancellationToken);
        stagedReset.IsLocked.ShouldBeFalse();

        await services[2].RecordFailureAsync(
            email,
            ip,
            now + Duration.FromSeconds(1),
            TestContext.Current.CancellationToken);

        await Should.ThrowAsync<DbUpdateConcurrencyException>(
            () => resetContext.CommitAsync(TestContext.Current.CancellationToken));

        var lockout = await ReadLockoutAsync(email, ip);
        lockout.Version.ShouldBe(2u);
        lockout.FailedCount.ShouldBe(2);
        lockout.LockedUntil.ShouldBeNull();
    }

    [Fact]
    public async Task When_FirstSuccessfulResetRacesWithFirstFailure_Then_AbsentStateIsFenced()
    {
        apiFactory.GuidProvider.Generate().Returns(_ => Guid.NewGuid());
        var email = $"concurrent-first-reset-{Guid.NewGuid():N}@example.com";
        var ip = $"192.0.2.{Random.Shared.Next(1, 255)}";
        var now = apiFactory.FakeClock.GetCurrentInstant();
        var services = CreateThrottleServices(5);

        await using var resetScope = apiFactory.Services.CreateAsyncScope();
        var resetContext = resetScope.ServiceProvider.GetRequiredService<IdentityDomainWriteContext>();
        var stagedReset = await services[0].StageResetAsync(
            resetContext,
            email,
            ip,
            now,
            TestContext.Current.CancellationToken);
        stagedReset.IsLocked.ShouldBeFalse();

        await services[1].RecordFailureAsync(
            email,
            ip,
            now,
            TestContext.Current.CancellationToken);

        var conflict = await Should.ThrowAsync<DbUpdateException>(
            () => resetContext.CommitAsync(TestContext.Current.CancellationToken));
        LoginProtectionConcurrency.IsAuthenticationFenceConflict(conflict).ShouldBeTrue();

        var lockout = await ReadLockoutAsync(email, ip);
        lockout.Version.ShouldBe(1u);
        lockout.FailedCount.ShouldBe(1);
    }

    [Fact]
    public async Task When_EmptyResetRacesWithFailure_Then_ExistingStateIsFenced()
    {
        apiFactory.GuidProvider.Generate().Returns(_ => Guid.NewGuid());
        var email = $"concurrent-empty-reset-{Guid.NewGuid():N}@example.com";
        var ip = $"192.0.2.{Random.Shared.Next(1, 255)}";
        var now = apiFactory.FakeClock.GetCurrentInstant();
        await SeedEmptyLockoutAsync(email, ip, now);
        var services = CreateThrottleServices(5);

        await using var resetScope = apiFactory.Services.CreateAsyncScope();
        var resetContext = resetScope.ServiceProvider.GetRequiredService<IdentityDomainWriteContext>();
        var stagedReset = await services[0].StageResetAsync(
            resetContext,
            email,
            ip,
            now + Duration.FromSeconds(1),
            TestContext.Current.CancellationToken);
        stagedReset.IsLocked.ShouldBeFalse();

        await services[1].RecordFailureAsync(
            email,
            ip,
            now + Duration.FromSeconds(1),
            TestContext.Current.CancellationToken);

        await Should.ThrowAsync<DbUpdateConcurrencyException>(
            () => resetContext.CommitAsync(TestContext.Current.CancellationToken));

        var lockout = await ReadLockoutAsync(email, ip);
        lockout.Version.ShouldBe(1u);
        lockout.FailedCount.ShouldBe(1);
        lockout.LockedUntil.ShouldBeNull();
    }

    [Fact]
    public async Task When_MultipleLimiterInstancesSharePartitions_Then_GlobalLimitIsEnforced()
    {
        apiFactory.GuidProvider.Generate().Returns(_ => Guid.NewGuid());
        var now = apiFactory.FakeClock.GetCurrentInstant();
        var ip = $"198.18.0.{Random.Shared.Next(1, 255)}";
        var email = $"rate-limit-{Guid.NewGuid():N}@example.com";
        var limiters = CreateRateLimiters(accountPermitLimit: 5, ipPermitLimit: 100);

        var leases = await Task.WhenAll(Enumerable.Range(0, 6).Select(index =>
            limiters[index % limiters.Length].AcquireLoginAsync(
                email,
                ip,
                now,
                TestContext.Current.CancellationToken)));

        leases.Count(lease => lease.IsAcquired).ShouldBe(5);
        leases.Count(lease => !lease.IsAcquired).ShouldBe(1);
        leases.Single(lease => !lease.IsAcquired).RetryAfterSeconds.ShouldBe(60);

        var otherPartition = await limiters[0].AcquireLoginAsync(
            $"other-{Guid.NewGuid():N}@example.com",
            ip,
            now,
            TestContext.Current.CancellationToken);
        otherPartition.IsAcquired.ShouldBeTrue();

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        var keys = await readContext.LoginRateLimitBuckets
            .Where(bucket => bucket.UpdatedAt == now)
            .Select(bucket => bucket.PartitionKey)
            .ToListAsync(TestContext.Current.CancellationToken);
        keys.ShouldNotBeEmpty();
        keys.ShouldAllBe(key => key.Length == 64 && key.All(Uri.IsHexDigit));
        keys.ShouldAllBe(key => !key.Contains(email, StringComparison.OrdinalIgnoreCase));
        keys.ShouldAllBe(key => !key.Contains(ip, StringComparison.Ordinal));
    }

    [Fact]
    public async Task When_RateLimitBucketsExpire_Then_CleanupRemovesThemInBoundedBatches()
    {
        var now = apiFactory.FakeClock.GetCurrentInstant();
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IdentityDomainWriteContext>();
        var expired = Enumerable.Range(0, 3)
            .Select(_ => LoginRateLimitBucket.Create(
                Guid.NewGuid(),
                RandomPartitionKey(),
                now - Duration.FromMinutes(16)))
            .ToArray();
        var active = LoginRateLimitBucket.Create(
            Guid.NewGuid(),
            RandomPartitionKey(),
            now - Duration.FromMinutes(14));
        writeContext.AddRange([.. expired, active]);
        await writeContext.CommitAsync(TestContext.Current.CancellationToken);
        writeContext.Clear();

        var job = new CleanupLoginRateLimitBucketsJob(
            writeContext,
            Options.Create(new CleanupLoginRateLimitBucketsJobOptions
            {
                RetentionMinutes = 15,
                BatchSize = 2,
            }),
            Options.Create(new LoginThrottleOptions
            {
                RateLimitWindowSeconds = 60,
            }),
            apiFactory.FakeClock);
        await job.ExecuteAsync(TestContext.Current.CancellationToken);

        await using var assertionScope = apiFactory.Services.CreateAsyncScope();
        var readContext = assertionScope.ServiceProvider.GetRequiredService<IdentityDomainReadContext>();
        var expiredIds = expired.Select(item => item.Id).ToArray();
        var remainingIds = await readContext.LoginRateLimitBuckets
            .Where(bucket => bucket.Id == active.Id || expiredIds.Contains(bucket.Id))
            .Select(bucket => bucket.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
        remainingIds.ShouldBe([active.Id]);
    }

    [Fact]
    public async Task When_ConfiguredRetentionIsShorterThanLimiterWindow_Then_ActiveBucketIsPreserved()
    {
        var now = apiFactory.FakeClock.GetCurrentInstant();
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IdentityDomainWriteContext>();
        var expired = LoginRateLimitBucket.Create(
            Guid.NewGuid(),
            RandomPartitionKey(),
            now - Duration.FromSeconds(121));
        var active = LoginRateLimitBucket.Create(
            Guid.NewGuid(),
            RandomPartitionKey(),
            now - Duration.FromSeconds(90));
        writeContext.AddRange([expired, active]);
        await writeContext.CommitAsync(TestContext.Current.CancellationToken);
        writeContext.Clear();

        var job = new CleanupLoginRateLimitBucketsJob(
            writeContext,
            Options.Create(new CleanupLoginRateLimitBucketsJobOptions
            {
                RetentionMinutes = 1,
                BatchSize = 2,
            }),
            Options.Create(new LoginThrottleOptions
            {
                RateLimitWindowSeconds = 120,
            }),
            apiFactory.FakeClock);
        await job.ExecuteAsync(TestContext.Current.CancellationToken);

        await using var assertionScope = apiFactory.Services.CreateAsyncScope();
        var readContext = assertionScope.ServiceProvider.GetRequiredService<IdentityDomainReadContext>();
        var remainingIds = await readContext.LoginRateLimitBuckets
            .Where(bucket => bucket.Id == active.Id || bucket.Id == expired.Id)
            .Select(bucket => bucket.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
        remainingIds.ShouldBe([active.Id]);
    }

    private LoginThrottleService[] CreateThrottleServices(int maxAttempts)
    {
        var scopeFactory = apiFactory.Services.GetRequiredService<IServiceScopeFactory>();
        var options = Options.Create(new LoginThrottleOptions
        {
            MaxAttempts = maxAttempts,
            WindowMinutes = 15,
            LockoutMinutes = 15,
            ConcurrencyRetryLimit = 64,
        });

        return Enumerable.Range(0, maxAttempts)
            .Select(_ => new LoginThrottleService(scopeFactory, apiFactory.GuidProvider, options))
            .ToArray();
    }

    private LoginRateLimiter[] CreateRateLimiters(int accountPermitLimit, int ipPermitLimit)
    {
        var scopeFactory = apiFactory.Services.GetRequiredService<IServiceScopeFactory>();
        var passwordOptions = apiFactory.Services.GetRequiredService<IOptions<PasswordAuthOptions>>();
        var throttleOptions = Options.Create(new LoginThrottleOptions
        {
            LoginAccountPermitLimit = accountPermitLimit,
            LoginIpPermitLimit = ipPermitLimit,
            TotpAccountPermitLimit = accountPermitLimit,
            TotpIpPermitLimit = ipPermitLimit,
            RateLimitWindowSeconds = 60,
            ConcurrencyRetryLimit = 64,
        });

        return Enumerable.Range(0, 2)
            .Select(_ => new LoginRateLimiter(
                scopeFactory,
                apiFactory.GuidProvider,
                throttleOptions,
                passwordOptions))
            .ToArray();
    }

    private async Task SeedEmptyLockoutAsync(string email, string ip, Instant now)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IdentityDomainWriteContext>();
        writeContext.Add(LoginLockout.Create(Guid.NewGuid(), email, ip, now));
        await writeContext.CommitAsync(TestContext.Current.CancellationToken);
    }

    private async Task<LoginLockout> ReadLockoutAsync(string email, string ip)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<IdentityDomainReadContext>();
        return await readContext.LoginLockouts.SingleAsync(
            lockout => lockout.Email == email && lockout.IpAddress == ip,
            TestContext.Current.CancellationToken);
    }

    private static string RandomPartitionKey() =>
        $"{Guid.NewGuid():N}{Guid.NewGuid():N}";
}
