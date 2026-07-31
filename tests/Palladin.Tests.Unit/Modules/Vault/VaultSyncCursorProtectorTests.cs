using NodaTime;
using NodaTime.Testing;
using Palladin.Core.Security;
using Palladin.Module.Vault.Infrastructure.Sync;

namespace Palladin.Tests.Unit.Modules.Vault;

public sealed class VaultSyncCursorProtectorTests
{
    private readonly FakeClock clock = new(Instant.FromUtc(2026, 7, 18, 18, 0));
    private readonly VaultSyncCursorContext memberContext = new(
        VaultSyncPrincipalType.Member,
        Guid.Parse("11111111-1111-4111-8111-111111111111"),
        Guid.Parse("22222222-2222-4222-8222-222222222222"),
        Guid.Parse("33333333-3333-4333-8333-333333333333"),
        VaultSyncAudience.Member,
        1);

    [Fact]
    public void When_SnapshotCursorIsAuthenticAndInScope_Then_RoundTripsItsBoundary()
    {
        // Given
        var protector = CreateProtector();
        var expected = new SnapshotCursorPayload(
            18446744073709551614,
            Guid.Parse("44444444-4444-4444-8444-444444444444"));

        // When
        var token = protector.ProtectSnapshot(memberContext, expected);
        var accepted = protector.TryUnprotectSnapshot(token, memberContext, out var actual);

        // Then
        accepted.ShouldBeTrue();
        actual.ShouldBe(expected);
    }

    [Fact]
    public void When_CursorIsTamperedOrReplayedAcrossPrincipalVaultAudienceOrKind_Then_FailsClosed()
    {
        // Given
        var protector = CreateProtector();
        var snapshot = protector.ProtectSnapshot(
            memberContext,
            new SnapshotCursorPayload(42, Guid.Parse("44444444-4444-4444-8444-444444444444")));
        var delta = protector.ProtectDelta(memberContext, new DeltaCursorPayload(10, 12, 20));
        var tampered = $"{snapshot[..^1]}{(snapshot[^1] == 'A' ? 'B' : 'A')}";
        var foreignPrincipal = memberContext with { PrincipalId = Guid.NewGuid() };
        var foreignVault = memberContext with { VaultId = Guid.NewGuid() };
        var nextKeyGeneration = memberContext with { KeyGeneration = 2 };
        var foreignAudience = memberContext with
        {
            PrincipalType = VaultSyncPrincipalType.Agent,
            Audience = VaultSyncAudience.Discovery,
        };

        // When / Then
        protector.TryUnprotectSnapshot(tampered, memberContext, out _).ShouldBeFalse();
        protector.TryUnprotectSnapshot(snapshot, foreignPrincipal, out _).ShouldBeFalse();
        protector.TryUnprotectSnapshot(snapshot, foreignVault, out _).ShouldBeFalse();
        protector.TryUnprotectSnapshot(snapshot, nextKeyGeneration, out _).ShouldBeFalse();
        protector.TryUnprotectSnapshot(snapshot, foreignAudience, out _).ShouldBeFalse();
        protector.TryUnprotectDelta(snapshot, memberContext, out _).ShouldBeFalse();
        protector.TryUnprotectSnapshot(delta, memberContext, out _).ShouldBeFalse();
    }

    [Fact]
    public void When_CursorExpires_Then_FailsClosed()
    {
        // Given
        var protector = CreateProtector();
        var token = protector.ProtectDelta(memberContext, new DeltaCursorPayload(10, 12, 20));
        clock.Advance(Duration.FromSeconds(VaultSyncProtocol.CursorTtlSeconds));

        // When
        var accepted = protector.TryUnprotectDelta(token, memberContext, out _);

        // Then
        accepted.ShouldBeFalse();
    }

    private VaultSyncCursorProtector CreateProtector()
        => new(new TestServerKeyDeriver(), clock);

    private sealed class TestServerKeyDeriver : IServerKeyDeriver
    {
        public byte[] DeriveHmacKey(string purpose) =>
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(purpose));
    }
}
