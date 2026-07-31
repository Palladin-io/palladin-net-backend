using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Palladin.Module.Search.Domain;
using Palladin.Module.Search.Contracts.Commands;
using Palladin.Module.Search.Contracts.ValueObjects;
using Palladin.Module.Search.Infrastructure.Persistence;

namespace Palladin.Tests.Integrations.Shared.Seeders;

internal static class SearchSeeder
{
    public static Task<SearchItem> SeedSearchAgentAsync(
        this IServiceProvider services, Guid organizationId, string name, Guid? id = null) =>
        SeedAsync(services, organizationId, id ?? Guid.NewGuid(), SearchItemTypes.Agent, name, name);

    public static Task<SearchItem> SeedSearchMemberAsync(
        this IServiceProvider services, Guid organizationId, string name, string email, Guid? id = null) =>
        SeedAsync(services, organizationId, id ?? Guid.NewGuid(), SearchItemTypes.Member, name, $"{name} {email}");

    private static async Task<SearchItem> SeedAsync(
        IServiceProvider services, Guid organizationId, Guid id, string type, string name, string searchText)
    {
        var item = SearchItem.Create(id, organizationId, type, name, searchText, SystemClock.Instance.GetCurrentInstant());
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<SearchDbWriteContext>();
        await context.Items.Where(x => x.OrganizationId == organizationId && x.Id == id).ExecuteDeleteAsync();
        context.Items.Add(item);
        await context.SaveChangesAsync();
        return item;
    }
}
