using JetBrains.Annotations;
using Palladin.Core.Events;

namespace Palladin.Module.PublicAssetCatalog.Contracts.Commands;

/// <summary>Legacy pre-production contract retained so queued envelopes keep their original schema.</summary>
[PublicAPI]
public sealed record AcquireWebsiteIconCommand(string Hostname) : IIntegrationCommand;

/// <summary>Durable request to fill a previously reserved website-icon asset.</summary>
[PublicAPI]
public sealed record AcquireWebsiteIconV2Command(Guid AssetId, string Hostname) : IIntegrationCommand;
