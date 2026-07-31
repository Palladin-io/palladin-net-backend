using System.Net;
using System.Net.Http.Json;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Agents.Infrastructure.AgentAuth;
using Palladin.Module.Identity;
using Palladin.Module.Notification.Features;
using Palladin.Module.Notification.Contracts.Commands;
using Palladin.Module.Notification.Contracts.ValueObjects;
using Palladin.Module.Notification.Infrastructure.Persistence;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Triggers;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Mocks;
using Palladin.Tests.Integrations.Shared.Seeders;
using FastEndpoints;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Vault;

[Collection<ApiFactoryCollection>]
public sealed class ReportCredentialFailureTests(ApiFactory apiFactory) : TestBase
{
    private async Task<(HttpClient AgentClient, Guid AgentId, Guid OrganizationId, Guid UserId, Guid VaultId, Guid EntryId)>
        SetupAsync()
    {
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var (_, plaintext) = await apiFactory.Services.SeedApiKeyAsync(organization.Id);
        var publicKey = AgentFaker.GeneratePublicKey();
        var signing = AgentRequestSigning.Generate();
        var authAgent = await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(
                    organizationId: organization.Id,
                    publicKey: publicKey,
                    signingPublicKey: signing.PublicKeyBase64)
                .RuleFor(x => x.Status, AgentStatus.Active));
        await apiFactory.Services.SeedVaultAgentAsync(organization.Id, status: AgentStatus.Active, id: authAgent.Id);

        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var entry = await apiFactory.Services.SeedEntryAsync(vault.Id, user.Id,
            EntryFaker.Create(vaultId: vault.Id, createdBy: user.Id));

        var client = apiFactory.CreateSignedAgentClient(authAgent.Id, plaintext, publicKey, signing);
        client.DefaultRequestHeaders.Add(AgentAuthenticationOptions.AgentHostnameHeader, "ci-runner-7");
        return (client, authAgent.Id, organization.Id, user.Id, vault.Id, entry.Id);
    }

    [Fact]
    public async Task When_AgentReportsFailure_Then_Persisted()
    {
        // Given
        var (client, agentId, orgId, _, vaultId, entryId) = await SetupAsync();
        var request = new ReportCredentialFailureRequest
        {
            VaultId = vaultId,
            EntryId = entryId,
            Code = CredentialFailureCode.LoginRejected,
        };

        // When
        var (response, result) = await client
            .POSTAsync<ReportCredentialFailureEndpoint, ReportCredentialFailureRequest, ReportCredentialFailureResponse>(request);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result!.ReportId.ShouldNotBe(Guid.Empty);

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        var stored = await readContext.CredentialFailureReports.FirstAsync(r => r.Id == result.ReportId);
        stored.OrganizationId.ShouldBe(orgId);
        stored.AgentId.ShouldBe(agentId);
        stored.VaultId.ShouldBe(vaultId);
        stored.EntryId.ShouldBe(entryId);
        stored.Code.ShouldBe(CredentialFailureCode.LoginRejected);
    }

    [Fact]
    public async Task When_LegacyFreeFormDiagnosticIsSubmitted_Then_RejectedAndNotPersisted()
    {
        var (client, _, _, _, vaultId, entryId) = await SetupAsync();

        var response = await client.PostAsJsonAsync(
            $"api/agent/vaults/{vaultId}/entries/{entryId}/credential-failure",
            new { code = "login_rejected", note = "plaintext account context" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<VaultDbReadContext>();
        (await readContext.CredentialFailureReports.AnyAsync(r => r.EntryId == entryId)).ShouldBeFalse();
    }

    [Fact]
    public async Task When_NoAgentAuth_Then_Unauthorized()
    {
        // Given
        var client = apiFactory.CreateClient();

        // When
        var response = await client.PostAsJsonAsync(
            $"api/agent/vaults/{Guid.NewGuid()}/entries/{Guid.NewGuid()}/credential-failure",
            new { code = "manual" });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task When_EntryNotInVault_Then_NotFound()
    {
        // Given
        var (client, _, _, _, _, entryId) = await SetupAsync();
        var request = new ReportCredentialFailureRequest
        {
            VaultId = Guid.NewGuid(),
            EntryId = entryId,
            Code = CredentialFailureCode.Manual,
        };

        // When
        var (response, _) = await client
            .POSTAsync<ReportCredentialFailureEndpoint, ReportCredentialFailureRequest, ReportCredentialFailureResponse>(request);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task When_FailureReported_Then_VaultMembersGetCredentialStale_WithMetadata_NoSecrets()
    {
        // Given
        var organizationId = Guid.NewGuid();
        var vaultId = Guid.NewGuid();
        var entryId = Guid.NewGuid();
        var member = Guid.NewGuid();
        await apiFactory.Services.SeedVaultScopeAsync(organizationId, vaultId, member);
        await apiFactory.Services.SeedNotificationUserAsync(organizationId, member, Permission.GrantManage);

        var failureEvent = new CredentialFailureReportedEvent(
            ReportId: Guid.NewGuid(),
            OrganizationId: organizationId,
            VaultId: vaultId,
            EntryId: entryId,
            AgentId: Guid.NewGuid(),
            Code: "login_rejected",
            UpdatedAt: Now());

        // When
        var command = await CaptureCommandAsync(failureEvent);
        command.ShouldNotBeNull();
        command.Type.ShouldBe(NotificationType.CredentialStale);
        command.TitleKey.ShouldBe("notification.credential_stale.title");

        command.Metadata.ShouldContainKey("errorHint");
        command.Metadata["errorHint"].ShouldBe("login_rejected");
        command.Metadata.Keys.ShouldNotContain(key =>
            new[] { "reason", "note", "entryLabel", "agentName", "host", "actionDeepLink" }.Contains(key));
        command.Metadata.Values.ShouldNotContain(v => v.Contains("password", StringComparison.OrdinalIgnoreCase));

        await apiFactory.ConsumeAsync<BroadcastNotificationConsumer, BroadcastNotificationCommand>(command);

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<NotificationDbReadContext>();
        var item = await readContext.InboxItems
            .FirstOrDefaultAsync(i => i.OrganizationId == organizationId && i.UserId == member);
        item.ShouldNotBeNull();
        item.Type.ShouldBe(NotificationType.CredentialStale);
        item.Metadata["vaultId"].ShouldBe(vaultId.ToString());
        item.Metadata["entryId"].ShouldBe(entryId.ToString());
    }

    private async Task<BroadcastNotificationCommand?> CaptureCommandAsync(CredentialFailureReportedEvent failureEvent)
    {
        BroadcastNotificationCommand? captured = null;
        var publishEndpoint = Substitute.For<IPublishEndpoint>();
        publishEndpoint
            .When(p => p.Publish(Arg.Any<BroadcastNotificationCommand>(), Arg.Any<CancellationToken>()))
            .Do(call => captured = call.Arg<BroadcastNotificationCommand>());

        var consumer = new OnCredentialFailureReportedBroadcast(publishEndpoint);
        await consumer.Consume(apiFactory.MockConsumeContext(failureEvent));
        return captured;
    }

    private static NodaTime.Instant Now() =>
        NodaTime.Instant.FromUnixTimeMilliseconds(
            NodaTime.SystemClock.Instance.GetCurrentInstant().ToUnixTimeMilliseconds());
}
