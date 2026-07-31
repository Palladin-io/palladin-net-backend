using System.Net;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Agents.Infrastructure.AgentAuth;
using Palladin.Module.Notification.Features;
using Palladin.Module.Notification.Infrastructure.Persistence;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Features;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Fakers;
using Palladin.Tests.Integrations.Shared.Seeders;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Notification;

[Collection<ApiFactoryCollection>]
public sealed class GrantApprovalCollapseE2ETests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_AgentRequestsThenUserApprovesViaRealFlow_Then_GrantPendingCollapses()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var entry = await apiFactory.Services.SeedEntryAsync(vault.Id, user.Id);
        await apiFactory.Services.SeedVaultScopeAsync(organization.Id, vault.Id, user.Id);
        await apiFactory.Services.SeedUserScopeAsync(organization.Id, user.Id);
        await apiFactory.Services.SeedNotificationUserAsync(organization.Id, user.Id, (Permission)int.MaxValue);

        var (_, apiKeyPlaintext) = await apiFactory.Services.SeedApiKeyAsync(organization.Id);
        var agentId = Guid.NewGuid();
        var provisioning = AgentDiscoveryProvisioningContractFaker.Create(organization.Id, vault.Id, agentId);
        var publicKey = provisioning.X25519PublicKey;
        var signing = provisioning.RequestSigning;
        var authAgent = await apiFactory.Services.SeedAgentAsync(
            organization.Id,
            AgentFaker.Create(
                    id: agentId,
                    organizationId: organization.Id,
                    publicKey: publicKey,
                    signingPublicKey: signing.PublicKeyBase64)
                .RuleFor(x => x.Status, AgentStatus.Active));
        await apiFactory.Services.SeedVaultAgentAsync(
            organization.Id, id: authAgent.Id, publicKey: publicKey,
            signingPublicKey: signing.PublicKeyBase64, iconKey: "smart_toy", iconColor: "#FF4F4F");
        await apiFactory.Services.SeedAgentDiscoveryProvisioningAsync(provisioning.Request, user.Id);

        var agentClient = apiFactory.CreateSignedAgentClient(authAgent.Id, apiKeyPlaintext, publicKey, signing);

        // When
        var (requestResponse, requestResult) = await agentClient
            .POSTAsync<GetOrRequestCredentialEndpoint, GetOrRequestCredentialRequest, GetOrRequestCredentialResponse>(
                new GetOrRequestCredentialRequest
                {
                    VaultId = vault.Id,
                    EntryId = entry.Id,
                    EncryptedReason = GrantEnvelopeTestData.EncryptedReason(
                        organization.Id, vault.Id, entry.Id, authAgent.Id, signing,
                        provisioning.Request.Manifest.VaultAgentMessageKeyFingerprint),
                });
        requestResponse.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var grantId = requestResult!.GrantId!.Value;

        await WaitForNotificationAsync(organization.Id, grantId, NotificationType.GrantPending);

        var userClient = apiFactory.CreateAuthenticatedClient(user);
        var (_, beforeApprove) = await userClient
            .GETAsync<ListNotificationsEndpoint, ListNotificationsRequest, ListNotificationsResponse>(
                new ListNotificationsRequest());
        beforeApprove!.Items.ShouldContain(i => i.Type == NotificationType.GrantPending);

        // When
        var approveResponse = await userClient
            .PUTAsync<ApproveGrantEndpoint, ApproveGrantRequest>(new ApproveGrantRequest
            {
                VaultId = vault.Id,
                GrantId = grantId,
                GrantEntry = GrantEnvelopeTestData.Contract(
                    organization.Id, vault.Id, grantId, entry.Id, publicKey, remainingUses: 5,
                    agentId: authAgent.Id),
                QueryLimit = 5,
            });
        approveResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await WaitForNotificationAsync(organization.Id, grantId, NotificationType.GrantApproved);

        // Then
        var (_, afterApprove) = await userClient
            .GETAsync<ListNotificationsEndpoint, ListNotificationsRequest, ListNotificationsResponse>(
                new ListNotificationsRequest());
        afterApprove!.Items.ShouldNotContain(i => i.Type == NotificationType.GrantPending);
        var approvedCard = afterApprove.Items.Single(i => i.Type == NotificationType.GrantApproved);
        approvedCard.ActionState.ShouldBeNull();

        // And
        approvedCard.Metadata["queryLimit"].ShouldBe("5");
        approvedCard.Metadata["queryCount"].ShouldBe("0");
        approvedCard.Metadata.ShouldNotContainKey("expiresAt");

        // And — presentation is resolved locally after unlock; the backend emits opaque references only.
        approvedCard.Metadata.ShouldContainKey("grantId");
        approvedCard.Metadata.ShouldContainKey("vaultId");
        approvedCard.Metadata.ShouldContainKey("agentId");
        approvedCard.Metadata.Keys.ShouldNotContain(key =>
            new[] { "agentName", "entryLabel", "vaultName", "agentIconKey", "agentIconColor" }.Contains(key));

        var (_, summary) = await userClient.GETAsync<GetNotificationSummaryEndpoint, NotificationSummaryResponse>();
        summary!.PendingActionCount.ShouldBe(0);
        summary.UnreadCount.ShouldBeGreaterThan(0);
        afterApprove.Items.Single(i => i.Type == NotificationType.GrantApproved).ReadAt.ShouldBeNull();
    }

    private async Task WaitForNotificationAsync(Guid organizationId, Guid subjectId, NotificationType type)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await using var scope = apiFactory.Services.CreateAsyncScope();
            var readContext = scope.ServiceProvider.GetRequiredService<NotificationDbReadContext>();
            var exists = await readContext.InboxItems.AnyAsync(i =>
                i.OrganizationId == organizationId && i.SubjectId == subjectId && i.Type == type);
            if (exists)
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Notification {type} for subject {subjectId} did not arrive in time.");
    }
}
