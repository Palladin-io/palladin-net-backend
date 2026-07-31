using System.Security.Cryptography;
using System.Text;
using NSubstitute;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Purge;

namespace Palladin.Tests.Unit.Modules.Vault;

public sealed class EntryPurgeTokenGeneratorTests
{
    [Fact]
    public async Task When_PurgeTokenIsGenerated_Then_ItMatchesTheFrozenCanonicalInput()
    {
        var key = Enumerable.Range(1, 32).Select(x => (byte)x).ToArray();
        var provider = Substitute.For<IEntryPurgeKeyProvider>();
        provider.GetKeyAsync(7, Arg.Any<CancellationToken>()).Returns(key);
        var generator = new EntryPurgeTokenGenerator(provider);
        var scope = new EntryScope(
            Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
            Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f"),
            Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100"));

        var token = await generator.GenerateAsync(scope, 7, TestContext.Current.CancellationToken);

        token.ShouldBe(ExpectedToken(scope, key));
        token.Length.ShouldBe(64);
        token.ShouldBe(token.ToLowerInvariant());
        await provider.Received(1).GetKeyAsync(7, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task When_AnyTenantScopeIdentifierChanges_Then_PurgeTokenChanges()
    {
        var provider = Substitute.For<IEntryPurgeKeyProvider>();
        provider.GetKeyAsync(1, Arg.Any<CancellationToken>()).Returns(new byte[32]);
        var generator = new EntryPurgeTokenGenerator(provider);
        var baseline = new EntryScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var candidates = new[]
        {
            baseline with { OrganizationId = Guid.NewGuid() },
            baseline with { VaultId = Guid.NewGuid() },
            baseline with { EntryId = Guid.NewGuid() },
        };

        var baselineToken = await generator.GenerateAsync(
            baseline,
            1,
            TestContext.Current.CancellationToken);
        foreach (var candidate in candidates)
        {
            (await generator.GenerateAsync(candidate, 1, TestContext.Current.CancellationToken))
                .ShouldNotBe(baselineToken);
        }
    }

    private static string ExpectedToken(EntryScope scope, byte[] key)
    {
        var prefix = Encoding.ASCII.GetBytes("PLDN-PURGE-ENTRY-v1");
        var input = new byte[prefix.Length + 48];
        prefix.CopyTo(input, 0);
        WriteGuid(scope.OrganizationId, input.AsSpan(prefix.Length, 16));
        WriteGuid(scope.VaultId, input.AsSpan(prefix.Length + 16, 16));
        WriteGuid(scope.EntryId, input.AsSpan(prefix.Length + 32, 16));
        return Convert.ToHexString(HMACSHA256.HashData(key, input)).ToLowerInvariant();
    }

    private static void WriteGuid(Guid value, Span<byte> destination) =>
        value.TryWriteBytes(destination, bigEndian: true, out _);
}
