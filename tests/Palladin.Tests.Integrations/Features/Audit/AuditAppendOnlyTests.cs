using Palladin.Module.Audit.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Audit;

[Collection<ApiFactoryCollection>]
public sealed class AuditPersistenceTests(ApiFactory apiFactory) : TestBase
{
    private async Task<Guid> SeedEntryAsync(Guid orgId)
    {
        await AuditSeeding.SeedGrantApprovedAsync(apiFactory, orgId, Guid.NewGuid(), Guid.NewGuid());

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<AuditDbReadContext>();
        return await readContext.AuditLogEntries
            .Where(e => e.OrganizationId == orgId)
            .Select(e => e.Id)
            .FirstAsync();
    }

    [Fact]
    public async Task When_InsertingAndSelecting_Then_StillWorks()
    {
        // Given
        var orgId = Guid.NewGuid();

        // When
        var id = await SeedEntryAsync(orgId);

        // Then
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<AuditDbReadContext>();
        (await readContext.AuditLogEntries.AnyAsync(e => e.Id == id)).ShouldBeTrue();
    }
}
