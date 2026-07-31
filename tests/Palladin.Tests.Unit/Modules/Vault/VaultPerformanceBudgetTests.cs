using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.History;
using Palladin.Module.Vault.Infrastructure.Sync;
using Palladin.Module.Vault.Shared;

namespace Palladin.Tests.Unit.Modules.Vault;

public sealed class VaultPerformanceBudgetTests
{
    public static TheoryData<int> RepresentativeVaultSizes => new()
    {
        1_000,
        10_000,
        20_000,
    };

    [Theory]
    [MemberData(nameof(RepresentativeVaultSizes))]
    public void When_RepresentativeVaultIsSynchronized_Then_ActualPageAssemblyIsBounded(int entryCount)
    {
        // Given
        var ciphertext = new string('A', 1_024);
        var deltaItems = Math.Max(1, entryCount / 100);

        // When
        var snapshot = AssemblePages(entryCount, ciphertext);
        var delta = AssemblePages(deltaItems, ciphertext);

        // Then
        Console.WriteLine(
            $"entries={entryCount};snapshotPages={snapshot.PageCount};deltaItems={deltaItems};deltaPages={delta.PageCount};maximumPageItems={snapshot.MaximumPageItems};maximumLogicalCiphertextBytes={snapshot.MaximumPageBytes}");
        snapshot.ProcessedItems.ShouldBe(entryCount);
        snapshot.PageCount.ShouldBe(DivideRoundUp(entryCount, VaultSyncProtocol.DefaultPageSize));
        snapshot.MaximumPageItems.ShouldBe(VaultSyncProtocol.DefaultPageSize);
        snapshot.MaximumPageBytes.ShouldBeLessThan(VaultSyncProtocol.OperationalResponseBytes);
        delta.ProcessedItems.ShouldBe(deltaItems);
        delta.PageCount.ShouldBe(DivideRoundUp(deltaItems, VaultSyncProtocol.DefaultPageSize));
        delta.MaximumPageItems.ShouldBeLessThanOrEqualTo(VaultSyncProtocol.DefaultPageSize);
        delta.MaximumPageBytes.ShouldBeLessThan(VaultSyncProtocol.OperationalResponseBytes);
    }

    [Fact]
    public void When_BackendBudgetsAreCompared_Then_EveryCollectionAndResponseHasACeiling()
    {
        // Given
        var history = new VaultHistoryOptions();

        // When
        var maximumSingleProjectionBytes = new[]
        {
            VaultProtocol.MaximumMemberIndexCiphertextBytes,
            VaultProtocol.MaximumMemberSecretCiphertextBytes,
            VaultProtocol.MaximumAgentDiscoveryCiphertextBytes,
            VaultProtocol.MaximumWrappedEntryKeyBytes,
        }.Max();

        // Then
        VaultSyncProtocol.MaxPageSize.ShouldBe(200);
        VaultSyncProtocol.OperationalResponseBytes.ShouldBe(2 * 1024 * 1024);
        VaultProtocol.MaximumRotationBatchItems.ShouldBe(100);
        history.DefaultPageSize.ShouldBe(20);
        history.MaximumPageSize.ShouldBe(100);
        history.MaximumVersions.ShouldBe(100);
        maximumSingleProjectionBytes.ShouldBeLessThan(VaultSyncProtocol.OperationalResponseBytes);
    }

    [Fact]
    public void When_InitialSyncContractsAreInspected_Then_SecretsGrantsAndHistoryStayOnDemand()
    {
        // Given
        var forbiddenFragments = new[] { "Secret", "Grant", "History", "VersionList" };

        // When
        var memberFields = typeof(MemberSyncItem).GetProperties().Select(property => property.Name).ToArray();
        var discoveryFields = typeof(AgentDiscoverySyncItem).GetProperties().Select(property => property.Name).ToArray();

        // Then
        memberFields.ShouldContain("EntryKey");
        memberFields.ShouldContain("MemberIndex");
        discoveryFields.ShouldContain("AgentDiscovery");
        memberFields.Concat(discoveryFields)
            .ShouldNotContain(name => forbiddenFragments.Any(name.Contains));
    }

    private static int DivideRoundUp(int value, int divisor) => (value + divisor - 1) / divisor;

    private static PageAssemblyResult AssemblePages(int totalItems, string ciphertext)
    {
        var processedItems = 0;
        var pageCount = 0;
        var maximumPageItems = 0;
        var maximumPageBytes = 0;
        while (processedItems < totalItems)
        {
            var items = new List<RepresentativeCiphertextItem>(VaultSyncProtocol.DefaultPageSize);
            var itemBytes = 0;
            while (processedItems < totalItems)
            {
                var item = new RepresentativeCiphertextItem(processedItems, ciphertext);
                if (!VaultSyncResponseBudget.TryAddItem(
                        new RepresentativePage([], "opaque-cursor"),
                        items,
                        ref itemBytes,
                        item,
                        VaultSyncProtocol.DefaultPageSize))
                {
                    break;
                }

                processedItems++;
            }

            items.ShouldNotBeEmpty();
            pageCount++;
            maximumPageItems = Math.Max(maximumPageItems, items.Count);
            maximumPageBytes = Math.Max(maximumPageBytes, itemBytes);
        }

        return new PageAssemblyResult(processedItems, pageCount, maximumPageItems, maximumPageBytes);
    }

    private sealed record RepresentativeCiphertextItem(int EntryNumber, string Ciphertext);

    private sealed record RepresentativePage(
        IReadOnlyCollection<RepresentativeCiphertextItem> Items,
        string? NextCursor);

    private sealed record PageAssemblyResult(
        int ProcessedItems,
        int PageCount,
        int MaximumPageItems,
        int MaximumPageBytes);
}
