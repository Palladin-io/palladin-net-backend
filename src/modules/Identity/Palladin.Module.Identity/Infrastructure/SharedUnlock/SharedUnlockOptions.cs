namespace Palladin.Module.Identity.Infrastructure.SharedUnlock;

internal sealed class SharedUnlockOptions
{
    public const string Position = "Modules:Identity:SharedUnlock";
    public bool Enabled { get; init; } = true;
}
