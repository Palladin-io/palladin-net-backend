using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;
using Palladin.Core.Security;
using Palladin.Core.Types.Exceptions;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
internal sealed class CreateDefaultVaultEndpoint(
    VaultDomainWriteContext domainWriteContext,
    IClock clock) : Endpoint<CreateVaultRequest, CreateVaultResponse>
{
    public override void Configure()
    {
        Post("api/account/default-vault");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.VaultCreate);
        this.RequireEmailVerified();
        Summary(summary =>
        {
            summary.Summary = "Create the current Member's encrypted default Vault";
            summary.Description = "Consumes the same server-issued challenge and zero-knowledge ciphertext contract as normal Vault creation. The backend alone assigns the default-Vault structural flag.";
        });
        Tags("Vault/Vaults");
    }

    public override async Task HandleAsync(CreateVaultRequest req, CancellationToken ct)
    {
        var userId = User.GetUserId()!.Value;
        var organizationId = User.GetOrganizationId()!.Value;

        if (await domainWriteContext.Vaults.AnyAsync(v => v.OrganizationId == organizationId && v.CreatedBy == userId && v.IsDefault, ct))
        {
            throw new DefaultVaultAlreadyExistsException();
        }

        try
        {
            var now = clock.GetCurrentInstant();
            await CreateVaultEndpoint.FenceOrganizationLifecycleAsync(
                domainWriteContext, organizationId, ct);
            await CreateVaultEndpoint.EnsureMemberIsNotRemovingAsync(
                domainWriteContext, organizationId, userId, ct);
            if (await domainWriteContext.Vaults.AnyAsync(
                    vault => vault.OrganizationId == organizationId
                             && vault.CreatedBy == userId
                             && vault.IsDefault,
                    ct))
            {
                throw new DefaultVaultAlreadyExistsException();
            }

            var challenge = await domainWriteContext.VaultCreationChallenges
                .SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.VaultId == req.VaultId, ct);
            if (challenge is null)
            {
                ThrowError("A valid server-issued Vault creation challenge is required.");
            }

            challenge!.Consume(organizationId, userId, now);
            var metadata = VaultEnvelopeContractMapper.ToDomain(req.MemberVaultMetadata);
            var creatorVaultKey = VaultEnvelopeContractMapper.ToDomain(req.CreatorVaultKey);
            var keyMaterial = new[] { VaultEnvelopeContractMapper.ToDomain(req.DiscoveryKey) }
                .Concat(req.VaultPrivateKeys.Select(VaultEnvelopeContractMapper.ToDomain))
                .ToArray();
            var agentMessagePublicKey = VaultEnvelopeContractMapper.ToDomain(req.VaultAgentMessagePublicKey,
                VaultPublicKeyKindContract.AgentMessageX25519, req.CurrentKeyEpoch.AgentMessageKeyVersion);
            var manifestSigningPublicKey = VaultEnvelopeContractMapper.ToDomain(req.VaultManifestSigningPublicKey,
                VaultPublicKeyKindContract.ManifestSigningEd25519, req.CurrentKeyEpoch.ManifestSigningKeyVersion);
            var memberKeyDirectory = await domainWriteContext.MemberKeyDirectory
                .SingleOrDefaultAsync(x => x.UserId == userId, ct);
            CreateVaultEndpoint.ValidateCreatorKeyDirectory(memberKeyDirectory, creatorVaultKey);
            var vault = Domain.Vault.CreateDefault(
                req.VaultId,
                organizationId,
                userId,
                User.GetDisplayName(),
                metadata,
                new MemberKeyGeneration(req.CreatorVaultKey.MemberKeyGeneration),
                VaultEnvelopeContractMapper.ToDomain(req.CurrentKeyEpoch),
                creatorVaultKey,
                keyMaterial,
                agentMessagePublicKey,
                manifestSigningPublicKey,
                now);

            domainWriteContext.Add(vault);
            await domainWriteContext.CommitAsync(ct);
            await Send.CreatedAtAsync<GetVaultEndpoint>(
                new { id = vault.Id },
                CreateVaultEndpoint.ToResponse(vault, vault.VaultMemberKeyEnvelopes.Single()),
                cancellation: ct);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: "PK_VaultOrganizationLifecycles",
            })
        {
            domainWriteContext.Clear();
            throw new VaultOrganizationLifecycleChangedException();
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            domainWriteContext.Clear();
            throw new DefaultVaultAlreadyExistsException();
        }
        catch (DbUpdateConcurrencyException)
        {
            domainWriteContext.Clear();
            throw new DefaultVaultAlreadyExistsException();
        }
    }
}
