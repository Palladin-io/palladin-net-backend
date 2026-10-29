using Palladin.Api.Framework;
using Serilog.Events;
using Serilog.Parsing;
using Shouldly;

namespace Palladin.Tests.Integrations.Framework;

public sealed class PostHogLogExportTests
{
    private static readonly HashSet<string> SourceContexts =
        ["Palladin.Module.PublicAssetCatalog.Features.AcquireWebsiteIconV2Consumer"];

    [Fact]
    public void When_SourceIsAllowListed_Then_EventIsExported() =>
        PostHogLogExport.IsExported(
            Event("Palladin.Module.PublicAssetCatalog.Features.AcquireWebsiteIconV2Consumer"), SourceContexts)
            .ShouldBeTrue();

    [Theory]
    [InlineData("Palladin.Module.Identity.Features.OAuthAuthenticateOperation")]
    [InlineData(null)]
    public void When_SourceIsNotAllowListed_Then_EventStaysLocal(string? sourceContext) =>
        PostHogLogExport.IsExported(Event(sourceContext), SourceContexts).ShouldBeFalse();

    private static LogEvent Event(string? sourceContext) =>
        new(
            DateTimeOffset.UtcNow,
            LogEventLevel.Warning,
            null,
            new MessageTemplateParser().Parse("Website icon acquisition delayed {WaitSeconds} {AssetId}"),
            sourceContext is null ? [] : [new LogEventProperty("SourceContext", new ScalarValue(sourceContext))]);
}
