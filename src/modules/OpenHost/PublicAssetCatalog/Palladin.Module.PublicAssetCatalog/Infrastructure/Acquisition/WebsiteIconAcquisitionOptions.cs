using JetBrains.Annotations;

namespace Palladin.Module.PublicAssetCatalog.Infrastructure.Acquisition;

[UsedImplicitly]
internal sealed class WebsiteIconAcquisitionOptions
{
    public const string Position = "Modules:PublicAssetCatalog:WebsiteIconAcquisition";

    public int QueueWaitWarningSeconds { get; init; } = 120;
}
