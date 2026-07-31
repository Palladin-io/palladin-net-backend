using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Palladin.Module.Vault.Infrastructure.Crypto;

internal static class CryptoModule
{
    internal static IServiceCollection AddVaultCrypto(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<VaultCryptoOptions>(
            configuration.GetSection(VaultCryptoOptions.Position));

        services.AddSingleton<IVaultEnvelopeSuite, XChaCha20Poly1305VaultEnvelopeSuite>();
        services.AddSingleton<IVaultCryptoSuiteRegistry, VaultCryptoSuiteRegistry>();

        return services;
    }
}
