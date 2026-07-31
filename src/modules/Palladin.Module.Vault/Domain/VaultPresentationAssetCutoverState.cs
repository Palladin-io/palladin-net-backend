using NodaTime;

namespace Palladin.Module.Vault.Domain;

internal sealed class VaultPresentationAssetCutoverState
{
    internal const string ZeroKnowledgeAssets = "zero-knowledge-assets";

    public string Id { get; private set; } = string.Empty;
    public Instant? LegacyObjectsPurgedAt { get; private set; }

    private VaultPresentationAssetCutoverState() { }

    internal void MarkLegacyObjectsPurged(Instant completedAt)
    {
        if (LegacyObjectsPurgedAt is null)
        {
            LegacyObjectsPurgedAt = completedAt;
        }
    }
}
