using System.Text.Json;
using NodaTime;
using Palladin.Core.Json;
using Palladin.Core.Types;
using Palladin.Module.Vault.Infrastructure.Sync;
using Palladin.Module.Vault.Shared;

namespace Palladin.Tests.Unit.Modules.Vault;

public sealed class VaultSyncContractMapperTests
{
    [Fact]
    public void When_SyncItemsAreSerialized_Then_UpdatedAtBelongsOnlyToMemberContract()
    {
        // Given
        var updatedAt = Instant.FromUtc(2026, 7, 26, 18, 30);
        var member = new MemberSyncItem(
            Guid.NewGuid(),
            VaultSyncProtocol.HeadKind,
            EntryState.Active,
            updatedAt,
            "1",
            "1",
            1,
            null,
            null);
        var discovery = new AgentDiscoverySyncItem(
            Guid.NewGuid(),
            VaultSyncProtocol.HeadKind,
            "1",
            null);

        // When
        using var memberJson = JsonDocument.Parse(JsonSerializer.Serialize(
            member,
            PalladinJsonSerializationSettings.DefaultOptions));
        using var discoveryJson = JsonDocument.Parse(JsonSerializer.Serialize(
            discovery,
            PalladinJsonSerializationSettings.DefaultOptions));

        // Then
        memberJson.RootElement.GetProperty("updatedAt").GetString().ShouldBe("2026-07-26T18:30:00Z");
        discoveryJson.RootElement.TryGetProperty("updatedAt", out _).ShouldBeFalse();
    }

    [Fact]
    public void When_MemberTombstoneIsMapped_Then_PresentationTimestampIsAbsent()
    {
        // Given
        var entryId = Guid.NewGuid();

        // When
        var tombstone = VaultSyncContractMapper.ToMemberTombstone(entryId);

        // Then
        tombstone.EntryId.ShouldBe(entryId);
        tombstone.Kind.ShouldBe(VaultSyncProtocol.TombstoneKind);
        tombstone.UpdatedAt.ShouldBeNull();
    }
}
