using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using Microsoft.Extensions.Options;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Purge;

internal interface IEntryPurgeKeyProvider
{
    Task<byte[]> GetKeyAsync(uint version, CancellationToken cancellationToken);
}

internal sealed class SsmEntryPurgeKeyProvider(
    IAmazonSimpleSystemsManagement ssm,
    IOptions<EntryPurgeLedgerOptions> options) : IEntryPurgeKeyProvider
{
    private readonly ConcurrentDictionary<uint, Lazy<Task<byte[]>>> _keys = new();

    public async Task<byte[]> GetKeyAsync(uint version, CancellationToken cancellationToken) =>
        await _keys.GetOrAdd(version, keyVersion => new Lazy<Task<byte[]>>(
                () => LoadAsync(keyVersion, CancellationToken.None)))
            .Value
            .WaitAsync(cancellationToken);

    private async Task<byte[]> LoadAsync(uint version, CancellationToken cancellationToken)
    {
        var prefix = options.Value.KeyParameterPrefix.TrimEnd('/');
        var response = await ssm.GetParameterAsync(new GetParameterRequest
        {
            Name = $"{prefix}/{version}",
            WithDecryption = true,
        }, cancellationToken);
        if (response.Parameter?.Type != ParameterType.SecureString)
        {
            throw new InvalidOperationException("Vault Entry purge key must be stored as SSM SecureString.");
        }

        var encoded = response.Parameter.Value
                      ?? throw new InvalidOperationException("Vault Entry purge key parameter has no value.");
        byte[] key;
        try
        {
            key = Convert.FromBase64String(encoded);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("Vault Entry purge key must be base64 encoded.", exception);
        }

        if (key.Length < 32)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new InvalidOperationException("Vault Entry purge key must contain at least 32 random bytes.");
        }

        return key;
    }
}

internal sealed class EntryPurgeTokenGenerator(IEntryPurgeKeyProvider keyProvider)
{
    private static readonly byte[] DomainSeparation = Encoding.ASCII.GetBytes("PLDN-PURGE-ENTRY-v1");

    internal async Task<string> GenerateAsync(
        EntryScope scope,
        uint keyVersion,
        CancellationToken cancellationToken)
    {
        var key = await keyProvider.GetKeyAsync(keyVersion, cancellationToken);
        Span<byte> input = stackalloc byte[DomainSeparation.Length + 48];
        DomainSeparation.CopyTo(input);
        WriteGuid(scope.OrganizationId, input[DomainSeparation.Length..]);
        WriteGuid(scope.VaultId, input[(DomainSeparation.Length + 16)..]);
        WriteGuid(scope.EntryId, input[(DomainSeparation.Length + 32)..]);
        var token = HMACSHA256.HashData(key, input);
        return Convert.ToHexString(token).ToLowerInvariant();
    }

    private static void WriteGuid(Guid value, Span<byte> destination)
    {
        if (!value.TryWriteBytes(destination[..16], bigEndian: true, out var bytesWritten)
            || bytesWritten != 16)
        {
            throw new InvalidOperationException("Unable to encode an Entry purge scope identifier.");
        }
    }
}
