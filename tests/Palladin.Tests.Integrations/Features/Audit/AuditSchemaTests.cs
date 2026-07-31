using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Palladin.Module.Audit.Domain;
using Palladin.Module.Audit.Contracts.Commands;
using Palladin.Module.Audit.Contracts.ValueObjects;
using Palladin.Module.Audit.Features;
using Palladin.Module.Audit.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Audit;

[Collection<ApiFactoryCollection>]
public sealed class AuditSchemaTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task CanonicalAuditModel_ContainsOnlyOpaqueVaultAndEntryReferences()
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuditDbWriteContext>();
        var entry = context.Model.FindEntityType(typeof(AuditLogEntry));
        entry.ShouldNotBeNull();

        entry.GetProperties().Select(x => x.Name).ShouldNotContain("EntryLabel");
        entry.GetProperties().Select(x => x.Name).ShouldNotContain("AgentReason");
        entry.FindProperty(nameof(AuditLogEntry.VaultId)).ShouldNotBeNull();
        entry.FindProperty(nameof(AuditLogEntry.EntryId)).ShouldNotBeNull();
        entry.FindProperty(nameof(AuditLogEntry.Result))!.GetProviderClrType().ShouldBe(typeof(short));
    }

    [Fact]
    public void PublicAuditContracts_DoNotExposeForbiddenPlaintextFields()
    {
        var forbidden = new[] { "EntryLabel", "AgentReason", "VaultName" };

        typeof(AppendAuditLogCommand).GetProperties().Select(x => x.Name)
            .ShouldNotContain(name => forbidden.Contains(name));
        typeof(AuditLogListItem).GetProperties().Select(x => x.Name)
            .ShouldNotContain(name => forbidden.Contains(name));
    }
}
