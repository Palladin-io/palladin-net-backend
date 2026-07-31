using JetBrains.Annotations;
using Palladin.Core.Events;

namespace Palladin.Module.PublicAssetCatalog.Contracts.Commands;

/// <summary>Durable request to acquire one public website icon by normalized hostname.</summary>
[PublicAPI]
public sealed record AcquireWebsiteIconCommand(Guid AssetId, string Hostname) : IIntegrationCommand;
