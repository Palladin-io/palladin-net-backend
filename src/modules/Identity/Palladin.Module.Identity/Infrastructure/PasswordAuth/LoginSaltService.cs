using System.Security.Cryptography;
using System.Text;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.Persistence;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Palladin.Module.Identity.Infrastructure.PasswordAuth;

[UsedImplicitly]
internal sealed class LoginSaltService(
    IdentityDbReadContext readContext,
    IOptions<PasswordAuthOptions> options) : ILoginSaltService
{
    public async Task<LoginBootstrap> GetBootstrapAsync(
        string normalizedEmail,
        string profileId,
        CancellationToken ct)
    {
        var stored = await readContext.Users
            .Where(u => u.Email == normalizedEmail
                        && u.PasswordCredential != null
                        && u.KdfProfileId == profileId)
            .Select(u => new LoginBootstrap(u.Id, u.PasswordCredential!.AuthSalt))
            .FirstOrDefaultAsync(ct);

        if (stored is not null)
        {
            return stored;
        }

        return new LoginBootstrap(
            PseudoAccountId(normalizedEmail, profileId),
            PseudoBytes("kdf-salt", normalizedEmail, profileId, options.Value.PseudoSaltLength));
    }

    private Guid PseudoAccountId(string normalizedEmail, string profileId)
    {
        var bytes = PseudoBytes("account-id", normalizedEmail, profileId, 16);
        try
        {
            bytes[6] = (byte)((bytes[6] & 0x0f) | 0x40);
            bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
            return new Guid(bytes, bigEndian: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private byte[] PseudoBytes(
        string purpose,
        string normalizedEmail,
        string profileId,
        int length)
    {
        var key = Encoding.UTF8.GetBytes(options.Value.EnumerationSecret);
        var input = Encoding.UTF8.GetBytes($"{purpose}\0{profileId}\0{normalizedEmail}");
        var mac = HMACSHA256.HashData(key, input);
        try
        {
            return mac[..length];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(mac);
        }
    }
}
