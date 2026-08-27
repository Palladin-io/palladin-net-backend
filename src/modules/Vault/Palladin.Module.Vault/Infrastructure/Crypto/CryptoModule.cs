using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Palladin.Module.Vault.Infrastructure.Crypto;

internal static class CryptoModule
{
    internal static IServiceCollection AddVaultCrypto(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<VaultCryptoOptions>()
            .Bind(configuration.GetSection(VaultCryptoOptions.Position))
            .Validate(options => options.MaxGrantEntriesPerGrant is > 0 and <= 500,
                "Grant crypto limits are outside the safe product bounds.")
            .ValidateOnStart();

        services.AddSingleton<IVaultEnvelopeSuite, XChaCha20Poly1305VaultEnvelopeSuite>();
        services.AddSingleton<IVaultCryptoSuiteRegistry, VaultCryptoSuiteRegistry>();

        return services;
    }
}
