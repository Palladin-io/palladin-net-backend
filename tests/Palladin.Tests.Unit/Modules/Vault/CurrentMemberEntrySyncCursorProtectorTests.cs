using NodaTime;
using NodaTime.Testing;
using Palladin.Core.Security;
using Palladin.Module.Vault.Infrastructure.Sync;

namespace Palladin.Tests.Unit.Modules.Vault;

public sealed class CurrentMemberEntrySyncCursorProtectorTests
{
    private readonly FakeClock clock = new(Instant.FromUtc(2026, 8, 29, 8, 0));
    private readonly CurrentMemberEntrySyncCursorContext context = new(
        Guid.Parse("44444444-4444-4444-8444-444444444444"),
        Guid.Parse("11111111-1111-4111-8111-111111111111"),
        7,
        Guid.Parse("22222222-2222-4222-8222-222222222222"),
        Guid.Parse("44444444-4444-4444-8444-444444444444"),
        4,
        3,
        1);

    [Fact]
    public void When_CursorsAreAuthenticAndInScope_Then_TheirStableBoundariesRoundTrip()
    {
        var protector = CreateProtector();
        var snapshot = new SnapshotCursorPayload(
            12,
            Guid.Parse("33333333-3333-4333-8333-333333333333"));
        var delta = new DeltaCursorPayload(12, 14, 18);

        protector.TryUnprotectSnapshot(
                protector.ProtectSnapshot(context, snapshot),
                context,
                out var actualSnapshot)
            .ShouldBe(CurrentMemberEntrySyncCursorReadResult.Valid);
        protector.TryUnprotectDelta(
                protector.ProtectDelta(context, delta),
                context,
                out var actualDelta)
            .ShouldBe(CurrentMemberEntrySyncCursorReadResult.Valid);
        actualSnapshot.ShouldBe(snapshot);
        actualDelta.ShouldBe(delta);
    }

    [Fact]
    public void When_SecurityGenerationChanges_Then_AuthenticatedCursorRequiresReset()
    {
        var protector = CreateProtector();
        var token = protector.ProtectDelta(context, new DeltaCursorPayload(12, 14, 18));

        protector.TryUnprotectDelta(
                token,
                context with { OrganizationMembershipGeneration = 8 },
                out _)
            .ShouldBe(CurrentMemberEntrySyncCursorReadResult.StateChanged);
        protector.TryUnprotectDelta(token, context with { MemberKeyGeneration = 5 }, out _)
            .ShouldBe(CurrentMemberEntrySyncCursorReadResult.StateChanged);
        protector.TryUnprotectDelta(token, context with { VaultKeyVersion = 4 }, out _)
            .ShouldBe(CurrentMemberEntrySyncCursorReadResult.StateChanged);
        protector.TryUnprotectDelta(token, context with { OfflinePolicyVersion = 2 }, out _)
            .ShouldBe(CurrentMemberEntrySyncCursorReadResult.StateChanged);
    }

    [Fact]
    public void When_CursorIsTamperedForeignExpiredOrMalformed_Then_FailsClosed()
    {
        var protector = CreateProtector();
        var token = protector.ProtectSnapshot(
            context,
            new SnapshotCursorPayload(12, Guid.Parse("33333333-3333-4333-8333-333333333333")));
        var tampered = $"{token[..^1]}{(token[^1] == 'A' ? 'B' : 'A')}";

        protector.TryUnprotectSnapshot(tampered, context, out _)
            .ShouldBe(CurrentMemberEntrySyncCursorReadResult.Invalid);
        protector.TryUnprotectSnapshot(token, context with { PrincipalId = Guid.NewGuid() }, out _)
            .ShouldBe(CurrentMemberEntrySyncCursorReadResult.Invalid);
        protector.TryUnprotectSnapshot(token, context with { OrganizationId = Guid.NewGuid() }, out _)
            .ShouldBe(CurrentMemberEntrySyncCursorReadResult.Invalid);
        protector.TryUnprotectSnapshot(token, context with { VaultId = Guid.NewGuid() }, out _)
            .ShouldBe(CurrentMemberEntrySyncCursorReadResult.Invalid);
        protector.TryUnprotectSnapshot(token, context with { MemberId = Guid.NewGuid() }, out _)
            .ShouldBe(CurrentMemberEntrySyncCursorReadResult.Invalid);

        clock.Advance(Duration.FromSeconds(CurrentMemberEntrySyncProtocol.CursorTtlSeconds));
        protector.TryUnprotectSnapshot(token, context, out _)
            .ShouldBe(CurrentMemberEntrySyncCursorReadResult.Invalid);
    }

    [Fact]
    public void When_DeltaBoundaryOrderingIsInvalid_Then_FailsClosed()
    {
        var protector = CreateProtector();
        var token = protector.ProtectDelta(context, new DeltaCursorPayload(14, 12, 18));

        protector.TryUnprotectDelta(token, context, out _)
            .ShouldBe(CurrentMemberEntrySyncCursorReadResult.Invalid);
    }

    private CurrentMemberEntrySyncCursorProtector CreateProtector() =>
        new(new TestServerKeyDeriver(), clock);

    private sealed class TestServerKeyDeriver : IServerKeyDeriver
    {
        public byte[] DeriveHmacKey(string purpose) =>
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.ASCII.GetBytes(purpose));
    }
}
