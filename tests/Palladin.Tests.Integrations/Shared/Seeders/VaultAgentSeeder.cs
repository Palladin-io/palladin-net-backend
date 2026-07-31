using Palladin.Core.Types;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared.Fakers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using VaultAgent = Palladin.Module.Vault.Domain.Agent;

namespace Palladin.Tests.Integrations.Shared.Seeders;

internal static class VaultAgentSeeder
{
    public static async Task<VaultAgent> SeedVaultAgentAsync(
        this IServiceProvider serviceProvider,
        Guid organizationId,
        AgentStatus status = AgentStatus.Active,
        Guid? id = null,
        string? publicKey = null,
        string? signingPublicKey = null,
        uint recipientKeyVersion = 1,
        string? iconKey = null,
        string? iconColor = null,
        Instant? updatedAt = null)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<VaultDbWriteContext>();

        var agent = VaultAgentFaker.Create(
            id: id,
            organizationId: organizationId,
            status: status,
            publicKey: publicKey,
            signingPublicKey: signingPublicKey,
            recipientKeyVersion: recipientKeyVersion,
            iconKey: iconKey,
            iconColor: iconColor,
            updatedAt: updatedAt).Generate();

        await writeContext.Agents.Where(x => x.Id == agent.Id).ExecuteDeleteAsync();
        writeContext.Agents.Add(agent);
        await writeContext.SaveChangesAsync();

        return agent;
    }
}
