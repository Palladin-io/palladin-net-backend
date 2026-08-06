using NodaTime;
using Palladin.Module.PublicAssetCatalog.Domain;
using Palladin.Module.PublicAssetCatalog.Features;
using Palladin.Module.PublicAssetCatalog.Infrastructure;
using System.Net;
using System.Text;
using System.Diagnostics;
using Palladin.Core.Events;
using Palladin.Module.PublicAssetCatalog.Contracts.Commands;
using Palladin.Module.PublicAssetCatalog.Infrastructure.Acquisition;
using Palladin.Module.PublicAssetCatalog.Infrastructure.Storage;
using NSubstitute;

namespace Palladin.Tests.Unit.PublicAssetCatalog;

public sealed class PublicAssetSecurityTests
{
    [Fact]
    public void When_Website_Icon_Is_Scheduled_Then_It_Uses_The_Durable_Command_Contract() =>
        new AcquireWebsiteIconV2Command(Guid.NewGuid(), "host-538.example.com").ShouldBeAssignableTo<IIntegrationCommand>();

    [Fact]
    public void When_Website_Icon_Is_Reserved_Then_Its_Delivery_Key_Is_Stable()
    {
        var assetId = Guid.Parse("11111111-2222-4333-8444-555555555555");

        PublicAssetContracts.WebsiteIconStorageKey(assetId)
            .ShouldBe("published/website-icon/11111111222243338444555555555555/1.png");
    }

    [Fact]
    public void When_A_Website_Icon_Is_Not_Ready_Then_Ensure_Does_Not_Return_An_Url()
    {
        var asset = PublicAsset.Create(Guid.NewGuid(), "Arbitrary display name", [("example.com", PublicAssetAliasKind.Hostname)]);
        var storage = Substitute.For<IPublicAssetStorage>();

        PublicAssetContracts.MapEnsuredWebsiteIcon(asset, storage).ShouldBeNull();
        PublicAssetContracts.MapWebsiteIconStatus(asset).ShouldBe("pending");
        asset.FailWebsiteIconAcquisition();
        PublicAssetContracts.MapEnsuredWebsiteIcon(asset, storage).ShouldBeNull();
        PublicAssetContracts.MapWebsiteIconStatus(asset).ShouldBe("failed");
    }

    [Fact]
    public void When_A_Pending_Acquisition_Is_Orphaned_Then_Ensure_Reschedules_It_Once_Per_Cooldown()
    {
        var asset = PublicAsset.Create(Guid.NewGuid(), "example.com", [("example.com", PublicAssetAliasKind.Hostname)]);
        var now = Instant.FromUnixTimeSeconds(100);

        asset.TryScheduleWebsiteIconAcquisition(now, Duration.FromSeconds(30)).ShouldBeTrue();
        asset.TryScheduleWebsiteIconAcquisition(now + Duration.FromSeconds(29), Duration.FromSeconds(30)).ShouldBeFalse();
        asset.TryScheduleWebsiteIconAcquisition(now + Duration.FromSeconds(30), Duration.FromSeconds(30)).ShouldBeTrue();
    }

    [Fact]
    public void When_A_Service_Upload_Is_Ready_Then_Ensure_Returns_The_Actual_Published_Revision()
    {
        var asset = PublicAsset.Create(Guid.NewGuid(), "Arbitrary display name", [("example.com", PublicAssetAliasKind.Hostname)]);
        asset.Publish(new string('a', 64), "image/png", 10, 16, 16, "published/digest/1.png", Instant.FromUnixTimeSeconds(1));
        var storage = Substitute.For<IPublicAssetStorage>();
        storage.GetDeliveryUrl("published/digest/1.png").Returns("https://assets.palladin.io/published/digest/1.png");

        var result = PublicAssetContracts.MapEnsuredWebsiteIcon(asset, storage);

        result.ShouldNotBeNull().Url.ShouldBe("https://assets.palladin.io/published/digest/1.png");
        PublicAssetContracts.MapWebsiteIconStatus(asset).ShouldBe("ready");
    }

    [Fact]
    public void When_A_Pending_Website_Upload_Is_Abandoned_Then_Its_Hostnames_Are_Released()
    {
        var asset = PublicAsset.Create(Guid.NewGuid(), "Arbitrary display name", [("example.com", PublicAssetAliasKind.Hostname)]);

        asset.AbandonPendingWebsiteUpload();

        asset.Status.ShouldBe(PublicAssetStatus.Deleted);
        asset.Aliases.ShouldBeEmpty();
        PublicAssetContracts.BuildHostnameMap([asset]).ShouldBeEmpty();
    }

    [Fact]
    public void When_Ensure_Reserves_New_Hostnames_Then_The_Member_Is_Charged_Per_Hostname()
    {
        using var limiter = new WebsiteIconEnsureLimiter();
        var member = Guid.NewGuid();

        limiter.TryAcquire(member, 499).ShouldBeTrue();
        limiter.TryAcquire(member, 1).ShouldBeTrue();
        limiter.TryAcquire(member, 1).ShouldBeFalse();
        limiter.TryAcquire(Guid.NewGuid(), 500).ShouldBeTrue();
    }

    [Fact]
    public async Task When_Dns_Or_Download_Does_Not_Complete_Then_The_Worker_Operation_Is_Bounded()
    {
        var elapsed = Stopwatch.StartNew();

        var result = await WebsiteIconAcquirer.RunBoundedAsync<byte[]>(
            token => new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(token),
            TimeSpan.FromMilliseconds(50),
            TestContext.Current.CancellationToken);

        elapsed.Stop();
        result.ShouldBeNull();
        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData("appleid.apple.com", "apple.com")]
    [InlineData("auth.eu.ovhcloud.com", "ovhcloud.com")]
    [InlineData("login.example.co.uk", "example.co.uk")]
    [InlineData("login.co.uk", null)]
    [InlineData("chomikuj.pl", null)]
    public void When_Subdomain_Icon_Is_Missing_Then_Only_One_Parent_Fallback_Is_Allowed(string hostname, string? expected) =>
        WebsiteIconAcquirer.ParentHostname(hostname).ShouldBe(expected);

    [Fact]
    public void When_Html_Declares_Several_Icon_Mechanisms_Then_They_Are_Resolved_And_Ranked()
    {
        const string html = """
            <base href="https://cdn.example.com/assets/">
            <link href=small.png sizes=16x16 rel="shortcut icon">
            <link rel="apple-touch-icon" sizes="180x180" href="touch.png">
            <meta content="/tile.png" name="msapplication-TileImage">
            """;

        var icons = WebsiteIconAcquirer.DiscoverHtmlIconUris(new Uri("https://example.com/"), html);

        icons.Take(2).ShouldBe(
        [
            new Uri("https://cdn.example.com/assets/small.png"),
            new Uri("https://cdn.example.com/assets/touch.png"),
        ]);
        icons.ShouldContain(new Uri("https://cdn.example.com/tile.png"));
    }

    [Fact]
    public void When_Manifest_Declares_Icons_Then_Largest_NonMonochrome_Asset_Is_Preferred()
    {
        var manifest = Encoding.UTF8.GetBytes("""
            {"icons":[
              {"src":"mono.svg","sizes":"any","purpose":"monochrome"},
              {"src":"icon-192.png","sizes":"192x192","purpose":"any"},
              {"src":"icon-512.png","sizes":"512x512","purpose":"maskable any"}
            ]}
            """);

        var icons = WebsiteIconAcquirer.DiscoverManifestIconUris(new Uri("https://example.com/app.webmanifest"), manifest);

        icons.ShouldBe(
        [
            new Uri("https://example.com/icon-512.png"),
            new Uri("https://example.com/icon-192.png"),
        ]);
    }

    [Fact]
    public void When_Page_Declares_Manifest_Then_Relative_Url_Is_Resolved()
    {
        var manifest = WebsiteIconAcquirer.DiscoverManifestUri(
            new Uri("https://example.com/account/login"),
            "<base href='https://static.example.com/app/'><link href='site.webmanifest' rel=manifest>");

        manifest.ShouldBe(new Uri("https://static.example.com/app/site.webmanifest"));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("169.254.169.254")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("100.64.0.1")]
    [InlineData("198.18.0.1")]
    [InlineData("::1")]
    [InlineData("fd00::1")]
    [InlineData("fe80::1")]
    [InlineData("2001:db8::1")]
    public void When_Address_Is_Not_Globally_Routable_Then_Outbound_Access_Is_Rejected(string value) =>
        PublicNetworkPolicy.IsPublic(IPAddress.Parse(value)).ShouldBeFalse();

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("2606:4700:4700::1111")]
    public void When_Address_Is_Public_Then_Outbound_Access_Is_Allowed(string value) =>
        PublicNetworkPolicy.IsPublic(IPAddress.Parse(value)).ShouldBeTrue();

    [Theory]
    [InlineData("localhost")]
    [InlineData("service.local")]
    [InlineData("metadata.internal")]
    [InlineData("127.0.0.1")]
    [InlineData("https://example.com/icon.png")]
    public void When_Hostname_Is_Not_Public_Dns_Name_Then_It_Is_Rejected(string value)
    {
        // Given / When
        var accepted = PublicAssetContracts.TryHostname(value, out _);

        // Then
        accepted.ShouldBeFalse();
    }

    [Fact]
    public void When_Ready_Asset_Is_Published_Again_Then_Immutable_Revision_Is_Protected()
    {
        // Given
        var asset = PublicAsset.Create(Guid.NewGuid(), "Example", [("example.com", PublicAssetAliasKind.Hostname)]);
        asset.Publish(new string('a', 64), "image/png", 10, 16, 16, "published/first.png", Instant.FromUnixTimeSeconds(1));

        // When
        var action = () => asset.Publish(new string('b', 64), "image/png", 11, 16, 16, "published/second.png", Instant.FromUnixTimeSeconds(2));

        // Then
        action.ShouldThrow<InvalidOperationException>();
    }

    [Fact]
    public void When_Historical_Assets_Share_A_Hostname_Then_Ensure_Map_Remains_Deterministic()
    {
        var first = PublicAsset.Create(Guid.Parse("10000000-0000-0000-0000-000000000000"), "First", [("bitmedia.io", PublicAssetAliasKind.Hostname)]);
        var second = PublicAsset.Create(Guid.Parse("20000000-0000-0000-0000-000000000000"), "Second", [("bitmedia.io", PublicAssetAliasKind.Hostname)]);
        first.Publish(new string('a', 64), "image/png", 10, 16, 16, "published/first.png", Instant.FromUnixTimeSeconds(1));
        second.Publish(new string('b', 64), "image/png", 10, 16, 16, "published/second.png", Instant.FromUnixTimeSeconds(2));

        var map = PublicAssetContracts.BuildHostnameMap([second, first]);

        map.ShouldHaveSingleItem().ShouldBe(new KeyValuePair<string, PublicAsset>("bitmedia.io", first));
    }

    [Fact]
    public void When_Upload_Session_Reaches_Expiry_Then_It_Is_Expired()
    {
        // Given
        var expiry = Instant.FromUnixTimeSeconds(10);
        var session = PublicAssetUploadSession.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new string('a', 64), "image/png", 10, expiry);

        // When / Then
        session.IsExpired(expiry).ShouldBeTrue();
    }

    [Fact]
    public void When_Agent_Icon_Is_Replaced_Then_Owner_Binding_Remains_And_Revision_Advances()
    {
        var organizationId = Guid.NewGuid(); var agentId = Guid.NewGuid();
        var asset = PublicAsset.CreateAgentIcon(Guid.NewGuid(), organizationId, agentId, "Agent icon");
        asset.Publish(new string('a', 64), "image/png", 10, 16, 16, "published/agent/1.png", Instant.FromUnixTimeSeconds(1));
        asset.Publish(new string('b', 64), "image/png", 11, 16, 16, "published/agent/2.png", Instant.FromUnixTimeSeconds(2));
        asset.OrganizationId.ShouldBe(organizationId);
        asset.OwnerId.ShouldBe(agentId);
        asset.CurrentRevision.ShouldBe(2);
    }

    [Fact]
    public void When_Service_Jti_Is_Replayed_Then_Second_Use_Is_Rejected()
    {
        var guard = new ServiceJwtReplayGuard();
        guard.TryConsume("one-time-jti", DateTimeOffset.UtcNow.AddMinutes(1)).ShouldBeTrue();
        guard.TryConsume("one-time-jti", DateTimeOffset.UtcNow.AddMinutes(1)).ShouldBeFalse();
    }

}
