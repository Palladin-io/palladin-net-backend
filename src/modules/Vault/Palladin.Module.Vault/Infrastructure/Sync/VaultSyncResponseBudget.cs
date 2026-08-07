using System.Text.Json;
using Palladin.Core.Json;

namespace Palladin.Module.Vault.Infrastructure.Sync;

internal static class VaultSyncResponseBudget
{
    internal static int MeasureItem<T>(T item) =>
        JsonSerializer.SerializeToUtf8Bytes(item, PalladinJsonSerializationSettings.DefaultOptions).Length;

    internal static bool FitsPage<TResponse>(
        TResponse responseWithEmptyItems,
        int itemBytes,
        int itemCount)
    {
        var emptyResponseBytes = JsonSerializer.SerializeToUtf8Bytes(
            responseWithEmptyItems,
            PalladinJsonSerializationSettings.DefaultOptions).Length;
        var separators = Math.Max(0, itemCount - 1);
        return emptyResponseBytes - 2 + itemBytes + separators <= VaultSyncProtocol.OperationalResponseBytes;
    }

    internal static bool TryAddItem<TResponse, TItem>(
        TResponse responseWithEmptyItems,
        List<TItem> items,
        ref int itemBytes,
        TItem item,
        int maximumItems)
    {
        if (items.Count >= maximumItems)
        {
            return false;
        }

        var addedItemBytes = MeasureItem(item);
        items.Add(item);
        itemBytes += addedItemBytes;
        if (FitsPage(responseWithEmptyItems, itemBytes, items.Count))
        {
            return true;
        }

        items.RemoveAt(items.Count - 1);
        itemBytes -= addedItemBytes;
        return false;
    }
}
