using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Palladin.Core.Security;
using Palladin.Module.Identity.Infrastructure.Options;

namespace Palladin.Module.Identity.Infrastructure.Jwt;

internal sealed class JwtServerKeyDeriver(IOptions<JwtOptions> options) : IServerKeyDeriver
{
    private readonly byte[] sourceKey = Encoding.UTF8.GetBytes(options.Value.Secret);

    public byte[] DeriveHmacKey(string purpose) =>
        HMACSHA256.HashData(sourceKey, Encoding.ASCII.GetBytes(purpose));
}
