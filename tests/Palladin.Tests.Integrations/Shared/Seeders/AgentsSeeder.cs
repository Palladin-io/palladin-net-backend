using Bogus;
using Palladin.Module.Agents.Domain;
using Palladin.Module.Agents.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared.Fakers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Palladin.Tests.Integrations.Shared.Seeders;

internal static class AgentsSeeder
{
    public static async Task<(ApiKey ApiKey, string Plaintext)> SeedApiKeyAsync(
        this IServiceProvider serviceProvider,
        Guid organizationId,
        string name = "Test Key",
        Guid? createdBy = null,
        Instant? revokedAt = null)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<AgentsDbWriteContext>();

        var now = SystemClock.Instance.GetCurrentInstant();
        var (apiKey, plaintext) = ApiKey.Generate(
            Guid.NewGuid(),
            organizationId,
            name,
            createdBy ?? Guid.NewGuid(),
            "Test Actor",
            now);

        if (revokedAt is not null)
        {
            apiKey.Revoke(Guid.NewGuid(), "Test Actor", revokedAt.Value);
        }

        await writeContext.ApiKeys
            .Where(x => x.Id == apiKey.Id)
            .ExecuteDeleteAsync();

        writeContext.ApiKeys.Add(apiKey);
        await writeContext.SaveChangesAsync();

        return (apiKey, plaintext);
    }

    public static async Task<User> SeedAgentsUserAsync(
        this IServiceProvider serviceProvider,
        Guid userId,
        string displayName = "Test User")
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<AgentsDbWriteContext>();

        var now = SystemClock.Instance.GetCurrentInstant();
        var user = User.Create(userId, displayName, now, now);

        await writeContext.Users
            .Where(x => x.Id == userId)
            .ExecuteDeleteAsync();

        writeContext.Users.Add(user);
        await writeContext.SaveChangesAsync();

        return user;
    }

    public static async Task<Agent> SeedAgentAsync(
        this IServiceProvider serviceProvider,
        Guid organizationId,
        Faker<Agent>? faker = null)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<AgentsDbWriteContext>();

        var agent = (faker ?? AgentFaker.Create(organizationId: organizationId))
            .RuleFor(x => x.OrganizationId, organizationId)
            .Generate();

        await writeContext.Agents
            .Where(x => x.Id == agent.Id)
            .ExecuteDeleteAsync();

        writeContext.Agents.Add(agent);
        await writeContext.SaveChangesAsync();

        return agent;
    }
}
