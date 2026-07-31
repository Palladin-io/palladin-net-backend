using System.Net;
using Palladin.Core.Security;
using Palladin.Module.Audit.Features;
using Palladin.Module.Audit.Domain;
using Palladin.Module.Audit.Contracts.Commands;
using Palladin.Module.Audit.Contracts.ValueObjects;
using Palladin.Module.Audit.Infrastructure.Persistence;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Features;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Triggers;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
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
public sealed class RecordVaultExportTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_VaultMember_RecordsExport_Then_Returns204()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var response = await client.POSTAsync<RecordVaultExportEndpoint, RecordVaultExportRequest>(
            new RecordVaultExportRequest { VaultId = vault.Id, Format = "csv", EntryCount = 12 });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task When_NonVaultMember_RecordsExport_Then_Returns403()
    {
        // Given
        var (owner, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, owner.Id);
        var outsider = await apiFactory.Services.SeedAdditionalOrganizationMemberAsync(organization.Id);
        var client = apiFactory.CreateAuthenticatedClient(outsider);

        // When
        var response = await client.POSTAsync<RecordVaultExportEndpoint, RecordVaultExportRequest>(
            new RecordVaultExportRequest { VaultId = vault.Id, Format = "json", EntryCount = 3 });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task When_InvalidFormat_Then_Returns400()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var response = await client.POSTAsync<RecordVaultExportEndpoint, RecordVaultExportRequest>(
            new RecordVaultExportRequest { VaultId = vault.Id, Format = "pdf", EntryCount = 1 });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task When_ExportEventConsumed_Then_WritesAuditLogWithoutSecrets()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var vault = await apiFactory.Services.SeedVaultAsync(organization.Id, user.Id);
        var evt = new VaultExportedEvent(vault.Id, user.Id, "Alice Owner", "csv", 7,
            apiFactory.FakeClock.GetCurrentInstant());

        AppendAuditLogCommand? captured = null;
        var publishEndpoint = Substitute.For<IPublishEndpoint>();
        publishEndpoint.When(p => p.Publish(Arg.Any<AppendAuditLogCommand>(), Arg.Any<CancellationToken>()))
            .Do(call => captured = call.Arg<AppendAuditLogCommand>());

        // When
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var readContext = scope.ServiceProvider.GetRequiredService<VaultDomainReadContext>();
            await new OnVaultExportedAudit(readContext, publishEndpoint).Consume(apiFactory.MockConsumeContext(evt));
        }

        captured.ShouldNotBeNull();
        await apiFactory.ConsumeAsync<AppendAuditLogConsumer, AppendAuditLogCommand>(captured);

        // Then
        await using var verifyScope = apiFactory.Services.CreateAsyncScope();
        var auditContext = verifyScope.ServiceProvider.GetRequiredService<AuditDbReadContext>();
        var entry = await auditContext.AuditLogEntries.FirstOrDefaultAsync(
            e => e.EventType == AuditEventType.VaultExported && e.VaultId == vault.Id);
        entry.ShouldNotBeNull();
        entry.ActorType.ShouldBe(AuditActorType.User);
        entry.UserId.ShouldBe(user.Id);
        entry.ActorName.ShouldBe("Alice Owner");
        entry.Metadata["format"].ShouldBe("csv");
        entry.Metadata["entryCount"].ShouldBe("7");
        string.Join("|", entry.Metadata.Keys).ToLowerInvariant().ShouldNotContain("blob");
    }
}
