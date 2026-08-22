using System.Security.Cryptography;
using System.Text;
using NodaTime;
using Palladin.Core.Events;
using Palladin.Module.Identity.Contracts.Events;

namespace Palladin.Module.Identity.Domain;

// Failed-login window per (email, ip). When failures reach the threshold within the
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
    public uint Version { get; private set; }

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

    internal LoginLockoutDecision RecordFailure(
        int maxAttempts,
        Duration window,
        Duration lockoutDuration,
        Guid attemptId,
        LoginFailureAttribution attribution,
        Instant now)
    {
        AddEvent(new LoginAttemptFailedEvent(
            attemptId,
            HashEmail(Email),
            IpAddress,
            attribution.OrganizationId,
            attribution.TargetUserId,
            attribution.Factor,
            now));

        if (IsLocked(now))
        {
            return LoginLockoutDecision.Locked(LockedUntil!.Value);
        }

        if (now - WindowStartedAt > window)
        {
            FailedCount = 0;
            WindowStartedAt = now;
        }

        FailedCount++;
        UpdatedAt = now;
        Version++;

        if (FailedCount >= maxAttempts)
        {
            var lockedUntil = now + lockoutDuration;
            LockedUntil = lockedUntil;
            FailedCount = 0;
            WindowStartedAt = now;
            AddEvent(new LoginLockedOutEvent(HashEmail(Email), lockedUntil, now));
            return LoginLockoutDecision.Locked(lockedUntil);
        }

        return LoginLockoutDecision.Recorded();
    }

    internal void Reset(Instant now)
    {
        FailedCount = 0;
        LockedUntil = null;
        WindowStartedAt = now;
        UpdatedAt = now;
        Version++;
    }

    private static string HashEmail(string email) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(email))).ToLowerInvariant();
}

internal readonly record struct LoginLockoutDecision(bool IsLocked, Instant? LockedUntil)
{
    internal static LoginLockoutDecision Recorded() => new(false, null);

    internal static LoginLockoutDecision Locked(Instant lockedUntil) => new(true, lockedUntil);
}

internal readonly record struct LoginFailureAttribution(
    Guid? OrganizationId,
    Guid? TargetUserId,
    string Factor)
{
    internal static LoginFailureAttribution Known(
        Guid organizationId,
        Guid targetUserId,
        string factor) =>
        new(organizationId, targetUserId, factor);

    internal static LoginFailureAttribution Unknown(string factor) =>
        new(null, null, factor);
}
