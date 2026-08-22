using System.Security.Cryptography;
using System.Text;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NodaTime;
using Palladin.Core.Guid;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.PasswordAuth;
using Palladin.Module.Identity.Infrastructure.Persistence;

namespace Palladin.Module.Identity.Infrastructure.Login;

[UsedImplicitly]
internal sealed class LoginRateLimiter(
    IServiceScopeFactory scopeFactory,
    IGuidProvider guidProvider,
    IOptions<LoginThrottleOptions> options,
    IOptions<PasswordAuthOptions> passwordAuthOptions) : ILoginRateLimiter
{
    public async Task<LoginRateLimitLease> AcquireLoginAsync(
        string normalizedEmail,
        string ipAddress,
        Instant now,
        CancellationToken ct)
    {
        var ipLease = await AcquireAsync(LoginRateLimitPartition.LoginIp, ipAddress, now, ct);
        return ipLease.IsAcquired
            ? await AcquireAsync(LoginRateLimitPartition.LoginAccount, normalizedEmail, now, ct)
            : ipLease;
    }

    public Task<LoginRateLimitLease> AcquireTotpIpAsync(
        string ipAddress,
        Instant now,
        CancellationToken ct) =>
        AcquireAsync(LoginRateLimitPartition.TotpIp, ipAddress, now, ct);

    public Task<LoginRateLimitLease> AcquireTotpAccountAsync(
        string normalizedEmail,
        Instant now,
        CancellationToken ct) =>
        AcquireAsync(LoginRateLimitPartition.TotpAccount, normalizedEmail, now, ct);

    private async Task<LoginRateLimitLease> AcquireAsync(
        LoginRateLimitPartition partition,
        string value,
        Instant now,
        CancellationToken ct)
    {
        var throttleOptions = options.Value;
        var partitionKey = HashPartition(partition, value);
        var permitLimit = PermitLimit(partition, throttleOptions);
        var window = Duration.FromSeconds(throttleOptions.RateLimitWindowSeconds);

        for (var attempt = 0; attempt < throttleOptions.ConcurrencyRetryLimit; attempt++)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var writeContext = scope.ServiceProvider.GetRequiredService<IdentityDomainWriteContext>();
            var bucket = await writeContext.LoginRateLimitBuckets
                .FirstOrDefaultAsync(x => x.PartitionKey == partitionKey, ct);

            if (bucket is null)
            {
                bucket = LoginRateLimitBucket.Create(guidProvider.Generate(), partitionKey, now);
                writeContext.Add(bucket);
            }

            var decision = bucket.TryAcquire(permitLimit, window, now);
            if (!decision.IsAcquired)
            {
                return LoginRateLimitLease.Rejected(decision.WindowEndsAt, now);
            }

            try
            {
                await writeContext.CommitAsync(ct);
                return LoginRateLimitLease.Acquired();
            }
            catch (Exception exception) when (LoginProtectionConcurrency.IsRetryable(exception))
            {
                await Task.Yield();
            }
        }

        return LoginRateLimitLease.FailClosed();
    }

    private string HashPartition(LoginRateLimitPartition partition, string value)
    {
        var key = Encoding.UTF8.GetBytes(passwordAuthOptions.Value.EnumerationSecret);
        var input = Encoding.UTF8.GetBytes($"auth-rate-limit\0{partition}\0{value}");
        var hash = HMACSHA256.HashData(key, input);
        try
        {
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    private static int PermitLimit(LoginRateLimitPartition partition, LoginThrottleOptions throttleOptions) =>
        partition switch
        {
            LoginRateLimitPartition.LoginIp => throttleOptions.LoginIpPermitLimit,
            LoginRateLimitPartition.LoginAccount => throttleOptions.LoginAccountPermitLimit,
            LoginRateLimitPartition.TotpIp => throttleOptions.TotpIpPermitLimit,
            LoginRateLimitPartition.TotpAccount => throttleOptions.TotpAccountPermitLimit,
            _ => throw new ArgumentOutOfRangeException(nameof(partition), partition, null),
        };
}

internal enum LoginRateLimitPartition
{
    LoginIp = 1,
    LoginAccount = 2,
    TotpIp = 3,
    TotpAccount = 4,
}
