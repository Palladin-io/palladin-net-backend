using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using NodaTime;
using Palladin.Core.Json;
using Palladin.Module.Identity.Contracts.ValueObjects;
using Palladin.Module.Vault.Infrastructure.Sync;
using Palladin.Module.Vault.Shared;

namespace Palladin.Tests.Unit.Modules.Vault;

public sealed class CurrentMemberEntrySyncProtocolConformanceTests
{
    private static readonly string FixtureRoot = Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "VaultProtocol2",
        "CurrentMemberEntrySync");

    [Fact]
    public void VendoredPolicy_MatchesFrozenProtocolTwoContract()
    {
        using var policy = Load("policy.json");
        var root = policy.RootElement;
        var negotiation = root.GetProperty("negotiation");
        var pagination = root.GetProperty("pagination");

        root.GetProperty("contract").GetString()
            .ShouldBe("palladin-vault-v2-current-member-entry-sync-policy");
        root.GetProperty("protocolVersion").GetUInt16().ShouldBe((ushort)2);
        root.GetProperty("policyVersion").GetUInt16().ShouldBe((ushort)2);
        negotiation.GetProperty("snapshot").GetProperty("route").GetString()
            .ShouldBe("/api/vaults/{vaultId}/current-entries/sync/snapshot");
        negotiation.GetProperty("delta").GetProperty("route").GetString()
            .ShouldBe("/api/vaults/{vaultId}/current-entries/sync/delta");
        pagination.GetProperty("defaultItemsOrJournalRows").GetInt32()
            .ShouldBe(CurrentMemberEntrySyncProtocol.DefaultPageSize);
        pagination.GetProperty("maximumItemsOrJournalRows").GetInt32()
            .ShouldBe(CurrentMemberEntrySyncProtocol.MaxPageSize);
        pagination.GetProperty("cursorTtlSeconds").GetInt32()
            .ShouldBe(CurrentMemberEntrySyncProtocol.CursorTtlSeconds);
    }

    [Fact]
    public void VendoredFixtures_AreByteExactAgainstTheirFrozenManifest()
    {
        using var manifest = Load(Path.Combine("fixtures", "manifest.json"));
        var root = manifest.RootElement;

        root.GetProperty("protocolVersion").GetUInt16().ShouldBe((ushort)2);
        root.GetProperty("syncPolicyVersion").GetUInt16().ShouldBe((ushort)2);
        root.GetProperty("syntheticDataOnly").GetBoolean().ShouldBeTrue();

        foreach (var file in root.GetProperty("files").EnumerateArray())
        {
            var relativePath = file.GetProperty("path").GetString()!;
            var bytes = File.ReadAllBytes(Path.Combine(FixtureRoot, "fixtures", relativePath));
            Convert.ToHexStringLower(SHA256.HashData(bytes))
                .ShouldBe(file.GetProperty("sha256").GetString());
        }

        var expectedPolicyHash = root.GetProperty("immutableDependencies")
            .EnumerateArray()
            .Single(item => item.GetProperty("path").GetString()!
                .EndsWith("current-member-entry-sync-policy.json", StringComparison.Ordinal))
            .GetProperty("sha256")
            .GetString();
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(FixtureRoot, "policy.json"))))
            .ShouldBe(expectedPolicyHash);
    }

    [Fact]
    public void NearLimitMemberSecret_RemainsOneCompleteBudgetedItem()
    {
        using var fixture = Load(Path.Combine("fixtures", "vectors", "near-limit-member-secret.json"));
        var root = fixture.RootElement;
        var response = root.GetProperty("response").Deserialize<CurrentMemberEntrySnapshotResponse>(
            PalladinJsonSerializationSettings.DefaultOptions)!;
        var item = response.Items.Single();
        var decodedPayload = WebEncoders.Base64UrlDecode(item.MemberSecret!.EncodedSuitePayload);

        decodedPayload.Length.ShouldBe(root
            .GetProperty("decodedMemberSecretEncodedSuitePayloadBytes")
            .GetInt32());
        root.GetProperty("decodedMemberSecretCiphertextBytes").GetInt32()
            .ShouldBe(262_144);
        var accepted = new List<CurrentMemberEntrySyncItem>();
        var itemBytes = 0;
        VaultSyncResponseBudget.TryAddItem(
                response with { Items = [] },
                accepted,
                ref itemBytes,
                item,
                CurrentMemberEntrySyncProtocol.MaxPageSize)
            .ShouldBeTrue();
        accepted.Single().ShouldBe(item);
    }

    [Theory]
    [InlineData(OrganizationOfflineAccessPolicy.Disabled, "disabled", 0)]
    [InlineData(OrganizationOfflineAccessPolicy.OneHour, "1h", 1)]
    [InlineData(OrganizationOfflineAccessPolicy.FourHours, "4h", 4)]
    [InlineData(OrganizationOfflineAccessPolicy.TwentyFourHours, "24h", 24)]
    public void OfflinePolicies_HaveOnlyTheFrozenFiniteWireValues(
        OrganizationOfflineAccessPolicy policy,
        string wireValue,
        int hours)
    {
        CurrentMemberEntrySyncProtocol.ToWireValue(policy).ShouldBe(wireValue);
        CurrentMemberEntrySyncProtocol.ToLeaseDuration(policy).ShouldBe(Duration.FromHours(hours));
    }

    private static JsonDocument Load(string relativePath) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureRoot, relativePath)));
}
