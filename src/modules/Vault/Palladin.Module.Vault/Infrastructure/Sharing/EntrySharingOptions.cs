namespace Palladin.Module.Vault.Infrastructure.Sharing;

internal sealed class EntrySharingOptions
{
    public const string Position = "Modules:Vault:EntrySharing";

    public int MaximumLifetimeHours { get; init; } = 168;
    public int MaximumReceipts { get; init; } = 100;
    public int SessionLifetimeSeconds { get; init; } = 900;
    public int OtpLifetimeSeconds { get; init; } = 300;
    public int OtpResendCooldownSeconds { get; init; } = 60;
    public int FailedAttemptLimit { get; init; } = 5;
    public int TotalFailedAttemptLimit { get; init; } = 25;
    public int LockoutSeconds { get; init; } = 900;
    public int PasswordHashIterations { get; init; } = 600_000;
    public int MinimumPasswordLength { get; init; } = 8;
    public int MaximumSecretLength { get; init; } = 128;
    public int MaximumActiveLinksPerEntry { get; init; } = 100;

    internal bool IsValid() => MaximumLifetimeHours is > 0 and <= 720
                              && MaximumReceipts is > 0 and <= 1_000
                              && SessionLifetimeSeconds is >= 60 and <= 3_600
                              && OtpLifetimeSeconds is >= 60 and <= 600
                              && OtpLifetimeSeconds <= SessionLifetimeSeconds
                              && OtpResendCooldownSeconds is >= 30 and <= 600
                              && FailedAttemptLimit is >= 1 and <= 10
                              && TotalFailedAttemptLimit >= FailedAttemptLimit
                              && TotalFailedAttemptLimit <= 100
                              && LockoutSeconds is >= 60 and <= 3_600
                              && PasswordHashIterations is >= 100_000 and <= 1_000_000
                              && MinimumPasswordLength is >= 8 and <= 32
                              && MaximumSecretLength >= MinimumPasswordLength
                              && MaximumSecretLength <= 256
                              && MaximumActiveLinksPerEntry is >= 1 and <= 500;
}
