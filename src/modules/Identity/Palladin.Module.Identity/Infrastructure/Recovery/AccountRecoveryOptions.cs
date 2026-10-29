using JetBrains.Annotations;

namespace Palladin.Module.Identity.Infrastructure.Recovery;

[UsedImplicitly]
internal sealed class AccountRecoveryOptions
{
    public const string Position = "Modules:Identity:AccountRecovery";

    public int ChallengeTtlSeconds { get; init; } = 300;
}
