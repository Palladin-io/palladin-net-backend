using System.Net;
using FastEndpoints;
using Shouldly;
using MassTransit;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Palladin.Core.Security;
using Palladin.Module.Identity.Contracts.Events;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Features;
using Palladin.Module.Identity.Triggers;
using Palladin.Module.Notification;
using Palladin.Module.Notification.Contracts.Commands;
using Palladin.Module.Notification.Features;
using Palladin.Module.Notification.Infrastructure.SignalR;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Mocks;
using Palladin.Tests.Integrations.Shared.Seeders;

namespace Palladin.Tests.Integrations.Features.Notification;

[Collection<ApiFactoryCollection>]
public sealed class RealtimeMemberCutoffTests(ApiFactory apiFactory)
{
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task When_MemberIsRemovedWhileConnected_Then_TheirConnectionIsClosed()
    {
        // Given
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var member = await apiFactory.Services.SeedAdditionalOrganizationMemberAsync(organization.Id);
        await using var memberConnection = await ConnectAsync(member);
        await using var ownerConnection = await ConnectAsync(owner);
        var memberClosed = ClosedSignal(memberConnection);
        var removal = new OrganizationMemberRemovalRequestedEvent(
            Guid.NewGuid(), organization.Id, member.Id, owner.Id, apiFactory.FakeClock.GetCurrentInstant());
        var triggerContext = apiFactory.MockConsumeContext(removal);
        DisconnectOrganizationMemberCommand? command = null;
        triggerContext
            .When(x => x.Publish(Arg.Any<DisconnectOrganizationMemberCommand>(), Arg.Any<CancellationToken>()))
            .Do(call => command = call.Arg<DisconnectOrganizationMemberCommand>());

        // When
        await new OnOrganizationMemberRemovalRequestedRealtime().Consume(triggerContext);
        await new DisconnectOrganizationMemberConsumer(
                apiFactory.Services.GetRequiredService<RealtimeConnectionRegistry>())
            .Consume(apiFactory.MockConsumeContext(command!));

        // Then
        command.ShouldBe(new DisconnectOrganizationMemberCommand(organization.Id, member.Id, removal.RequestedAt));
        (await memberClosed.WaitAsync(CloseTimeout, TestContext.Current.CancellationToken)).ShouldBeTrue();
        ownerConnection.State.ShouldBe(HubConnectionState.Connected);
    }

    [Fact]
    public async Task When_RemovingMemberConnects_Then_ConnectionIsClosed()
    {
        // Given
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var member = await apiFactory.Services.SeedAdditionalOrganizationMemberAsync(organization.Id);
        // An affected Vault keeps the staged removal open, so the membership stays Removing.
        await apiFactory.Services.SeedVaultAsync(organization.Id, member.Id);
        var token = AccessToken(member);
        var ownerClient = apiFactory.CreateAuthenticatedClient(owner, Permission.OrganizationManagement);
        (await ownerClient.DELETEAsync<RemoveOrganizationMemberEndpoint, RemoveOrganizationMemberRequest>(
                new RemoveOrganizationMemberRequest { UserId = member.Id }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // When
        await using var connection = Build(token);
        var closed = ClosedSignal(connection);
        await connection.StartAsync(TestContext.Current.CancellationToken);

        // Then
        (await closed.WaitAsync(CloseTimeout, TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    private async Task<HubConnection> ConnectAsync(User user)
    {
        var connection = Build(AccessToken(user));
        await connection.StartAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    private HubConnection Build(string token) => new HubConnectionBuilder()
        .WithUrl(new Uri(apiFactory.Server.BaseAddress, NotificationModule.HubPath), options =>
        {
            options.Transports = HttpTransportType.LongPolling;
            options.HttpMessageHandlerFactory = _ => apiFactory.Server.CreateHandler();
            options.AccessTokenProvider = () => Task.FromResult<string?>(token);
        })
        .Build();

    private string AccessToken(User user) =>
        apiFactory.CreateAuthenticatedClient(user).DefaultRequestHeaders.Authorization!.Parameter!;

    private static Task<bool> ClosedSignal(HubConnection connection)
    {
        var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += _ =>
        {
            closed.TrySetResult(true);
            return Task.CompletedTask;
        };
        return closed.Task;
    }
}
