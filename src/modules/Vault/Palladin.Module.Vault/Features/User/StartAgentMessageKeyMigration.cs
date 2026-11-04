using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Api;
using Palladin.Core.Cryptography;
using Palladin.Core.Guid;
using Palladin.Core.Security;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Authorization;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record StartAgentMessageKeyMigrationRequest : IRequiresVaultMembership
{
    public Guid VaultId { get; init; }
}

[PublicAPI]
internal sealed class StartAgentMessageKeyMigrationEndpoint(
    VaultDomainWriteContext domainWriteContext,
    IGuidProvider guidProvider,
    IClock clock) : Endpoint<StartAgentMessageKeyMigrationRequest, VaultKeyRotationResponse>
{
    public override void Configure()
    {
        Post("api/vaults/{vaultId:guid}/key-rotations/agent-message-key-migration");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.VaultManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(summary =>
        {
            summary.Summary = "Start replacing a legacy-suite Vault agent message key";
            summary.Description = "Creates a ProtocolMigration rotation with scope AgentMessage only: the Vault key, "
                + "VDK, manifest-signing key and Member envelopes stay unchanged. The client completes it through the "
                + "existing claim, batch and commit endpoints with a new agent message key on the current key-wrapper "
                + "suite (agentMessageKeyVersion + 1), its encrypted private key material and successor manifests "
                + "for every Agent. Pending reasons sealed to the retired key are invalidated at commit. An existing "
                + "uncommitted rotation is returned instead; a Vault already on the current suite returns 409 "
                + "agent-message-key-current.";
        });
        Tags("Vault/Key Rotation");
    }

    public override async Task HandleAsync(StartAgentMessageKeyMigrationRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId()!.Value;
        var userId = User.GetUserId()!.Value;
        var vault = await domainWriteContext.Vaults
            .Where(x => x.OrganizationId == organizationId && x.Id == req.VaultId)
            .Where(x => x.VaultMembers.Any(m => m.UserId == userId))
            .SingleOrDefaultAsync(ct);
        if (vault is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (vault.AgentMessageKeySuiteId == KeyWrapperSuites.Current.Id)
        {
            AddError(ErrorResponses.General("agent-message-key-current"));
            await Send.ErrorsAsync(StatusCodes.Status409Conflict, ct);
            return;
        }

        var rotation = await StartVaultKeyRotationEndpoint.StartOrGetActiveAsync(
            domainWriteContext,
            guidProvider.Generate(),
            vault,
            VaultKeyRotationCause.ProtocolMigration,
            VaultKeyRotationScope.AgentMessage,
            userId,
            clock.GetCurrentInstant(),
            ct);
        await Send.OkAsync(VaultKeyRotationResponses.Map(rotation), ct);
    }
}
