using System.Security.Cryptography;
using System.Text;
using Palladin.Core.Events;
using Palladin.Module.Identity.Contracts.Events;
using NodaTime;

namespace Palladin.Module.Identity.Domain;

// Sliding-window failed-login counter per (email, ip). When failures reach the threshold within the
// window the pair is locked until LockedUntil. A successful login resets it. Enumeration-safe: the
// same lockout applies whether or not the email maps to a real account.
internal sealed class LoginLockout : EventEntityBase
{
    public Guid Id { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public string IpAddress { get; private set; } = string.Empty;
    public int FailedCount { get; private set; }
    public Instant WindowStartedAt { get; private set; }
    public Instant? LockedUntil { get; private set; }
    public Instant CreatedAt { get; private set; }
    public Instant UpdatedAt { get; private set; }

    private LoginLockout() { }

    internal static LoginLockout Create(Guid id, string email, string ipAddress, Instant now) =>
        new()
        {
            Id = id,
            Email = email,
            IpAddress = ipAddress,
            WindowStartedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };

    internal bool IsLocked(Instant now) => LockedUntil is { } until && now < until;

    internal void RecordFailure(int maxAttempts, Duration window, Duration lockoutDuration, Instant now)
    {
        if (now - WindowStartedAt > window)
        {
            FailedCount = 0;
            WindowStartedAt = now;
        }

        FailedCount++;
        UpdatedAt = now;

        if (FailedCount >= maxAttempts)
        {
            var lockedUntil = now + lockoutDuration;
            LockedUntil = lockedUntil;
            FailedCount = 0;
            WindowStartedAt = now;
            AddEvent(new LoginLockedOutEvent(HashEmail(Email), lockedUntil, now));
        }
    }

    internal void Reset(Instant now)
    {
        FailedCount = 0;
        LockedUntil = null;
        WindowStartedAt = now;
        UpdatedAt = now;
    }

    private static string HashEmail(string email) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(email))).ToLowerInvariant();
}
