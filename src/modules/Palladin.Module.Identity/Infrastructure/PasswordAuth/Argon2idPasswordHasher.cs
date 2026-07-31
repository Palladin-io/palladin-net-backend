using System.Security.Cryptography;
using Konscious.Security.Cryptography;
using JetBrains.Annotations;
using Microsoft.Extensions.Options;

namespace Palladin.Module.Identity.Infrastructure.PasswordAuth;

[UsedImplicitly]
internal sealed class Argon2idPasswordHasher(IOptions<PasswordAuthOptions> options) : IPasswordHasher
{
    public (byte[] Hash, byte[] ServerSalt) Hash(byte[] clientAuthHash)
    {
        var serverSalt = RandomNumberGenerator.GetBytes(options.Value.ServerSaltLength);
        return (Compute(clientAuthHash, serverSalt), serverSalt);
    }

    public bool Verify(byte[] clientAuthHash, byte[] expectedHash, byte[] serverSalt)
    {
        var actual = Compute(clientAuthHash, serverSalt);
        return CryptographicOperations.FixedTimeEquals(actual, expectedHash);
    }

    private byte[] Compute(byte[] clientAuthHash, byte[] serverSalt)
    {
        var opts = options.Value;
        using var argon2 = new Argon2id(clientAuthHash)
        {
            Salt = serverSalt,
            MemorySize = opts.MemoryKib,
            Iterations = opts.Iterations,
            DegreeOfParallelism = opts.DegreeOfParallelism,
        };
        return argon2.GetBytes(opts.HashLength);
    }
}
