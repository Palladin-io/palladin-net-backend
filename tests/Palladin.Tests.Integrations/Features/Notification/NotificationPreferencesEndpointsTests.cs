using System.Net;
using Palladin.Core.Types;
using Palladin.Module.Notification.Features;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Seeders;
using FastEndpoints;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Notification;

[Collection<ApiFactoryCollection>]
public sealed class NotificationPreferencesEndpointsTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_GettingPreferences_Then_ReturnsOneItemPerTypeWithDefaults()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var (response, result) = await client
            .GETAsync<GetNotificationPreferencesEndpoint, GetNotificationPreferencesResponse>();

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var userFacingCount = Enum.GetValues<NotificationType>().Length - 2;
        result!.Items.Count.ShouldBe(userFacingCount);
        result.Items.ShouldNotContain(i => i.Type == NotificationType.AgentResolved);
        result.Items.ShouldNotContain(i => i.Type == NotificationType.AgentApproved);

        var grantPending = result.Items.Single(i => i.Type == NotificationType.GrantPending);
        grantPending.Mandatory.ShouldBeTrue();
        grantPending.InboxEnabled.ShouldBeTrue();
        grantPending.SignalREnabled.ShouldBeTrue();
        grantPending.PushEnabled.ShouldBeTrue();

        var grantApproved = result.Items.Single(i => i.Type == NotificationType.GrantApproved);
        grantApproved.Mandatory.ShouldBeFalse();
        grantApproved.InboxEnabled.ShouldBeTrue();
        grantApproved.SignalREnabled.ShouldBeTrue();
        grantApproved.PushEnabled.ShouldBeFalse();
    }

    [Fact]
    public async Task When_UpdatingMandatoryType_Then_InboxAndRealtimeStayOnButPushChanges()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var (response, result) = await client
            .PUTAsync<UpdateNotificationPreferencesEndpoint, UpdateNotificationPreferencesRequest, UpdateNotificationPreferencesResponse>(
                new UpdateNotificationPreferencesRequest
                {
                    Items =
                    [
                        new UpdateNotificationPreferenceItem
                        {
                            Type = NotificationType.GrantPending,
                            InboxEnabled = false,
                            SignalREnabled = false,
                            PushEnabled = false,
                        },
                    ],
                });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var grantPending = result!.Items.Single(i => i.Type == NotificationType.GrantPending);
        grantPending.InboxEnabled.ShouldBeTrue();
        grantPending.SignalREnabled.ShouldBeTrue();
        grantPending.PushEnabled.ShouldBeFalse();
    }

    [Fact]
    public async Task When_UpdatingNonMandatoryType_Then_AllChannelsHonoured()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var (_, result) = await client
            .PUTAsync<UpdateNotificationPreferencesEndpoint, UpdateNotificationPreferencesRequest, UpdateNotificationPreferencesResponse>(
                new UpdateNotificationPreferencesRequest
                {
                    Items =
                    [
                        new UpdateNotificationPreferenceItem
                        {
                            Type = NotificationType.CredentialStale,
                            InboxEnabled = false,
                            SignalREnabled = true,
                            PushEnabled = true,
                        },
                    ],
                });

        // Then
        var stale = result!.Items.Single(i => i.Type == NotificationType.CredentialStale);
        stale.InboxEnabled.ShouldBeFalse();
        stale.SignalREnabled.ShouldBeTrue();
        stale.PushEnabled.ShouldBeTrue();
    }
}
