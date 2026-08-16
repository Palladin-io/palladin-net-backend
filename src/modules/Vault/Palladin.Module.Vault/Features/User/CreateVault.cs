using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Security;
using Palladin.Core.Types.Exceptions;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record CreateVaultRequest
{
    public Guid VaultId { get; init; }
    public MemberVaultMetadataEnvelopeContract MemberVaultMetadata { get; init; } = null!;
    public VaultKeyEpochContract CurrentKeyEpoch { get; init; } = null!;
    public MemberVaultKeyEnvelopeContract CreatorVaultKey { get; init; } = null!;
    public VaultDiscoveryKeyEnvelopeContract DiscoveryKey { get; init; } = null!;
    public IReadOnlyList<VaultPrivateKeyEnvelopeContract> VaultPrivateKeys { get; init; } = [];
    public VaultPublicKeyContract VaultAgentMessagePublicKey { get; init; } = null!;
    public VaultPublicKeyContract VaultManifestSigningPublicKey { get; init; } = null!;
}

[PublicAPI]
public sealed record CreateVaultResponse(
    Guid Id,
    Guid OrganizationId,
    bool IsDefault,
    ushort ProtocolVersion,
    string MemberSequence,
    string DiscoverySequence,
    uint MemberKeyGeneration,
    VaultKeyEpochContract CurrentKeyEpoch,
    MemberVaultMetadataEnvelopeContract MemberVaultMetadata,
    MemberVaultKeyEnvelopeContract MemberVaultKey,
    VaultDiscoveryKeyEnvelopeContract DiscoveryKey,
    IReadOnlyList<VaultPrivateKeyEnvelopeContract> VaultPrivateKeys,
    VaultPublicKeyContract VaultAgentMessagePublicKey,
    VaultPublicKeyContract VaultManifestSigningPublicKey,
    Instant CreatedAt,
    Instant UpdatedAt);

[UsedImplicitly]
internal sealed class CreateVaultValidator : Validator<CreateVaultRequest>
{
    public CreateVaultValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.MemberVaultMetadata).NotNull().SetValidator(new MemberVaultMetadataContractValidator()!);
        RuleFor(x => x.CurrentKeyEpoch).NotNull();
        RuleFor(x => x.CreatorVaultKey).NotNull().SetValidator(new MemberVaultKeyContractValidator()!);
        RuleFor(x => x.DiscoveryKey).NotNull().SetValidator(new VaultDiscoveryKeyContractValidator()!);
        RuleFor(x => x.VaultPrivateKeys).NotNull().Must(x => x.Count == 2);
        RuleForEach(x => x.VaultPrivateKeys).SetValidator(new VaultPrivateKeyContractValidator());
        RuleFor(x => x.VaultAgentMessagePublicKey).NotNull();
        RuleFor(x => x.VaultManifestSigningPublicKey).NotNull();
    }
}

internal sealed class VaultDiscoveryKeyContractValidator : AbstractValidator<VaultDiscoveryKeyEnvelopeContract>
{
    internal VaultDiscoveryKeyContractValidator()
    {
        RuleFor(x => x.Descriptor).NotNull();
        RuleFor(x => x.Descriptor!.Scope).NotNull().SetValidator(new EnvelopeScopeContractValidator()!)
            .When(x => x.Descriptor is not null);
        RuleFor(x => x.Descriptor!.Binding).NotNull().When(x => x.Descriptor is not null);
        RuleFor(x => x.Descriptor!.MemberKeyGeneration).NotNull().When(x => x.Descriptor is not null);
        RuleFor(x => x.OrganizationId).NotEmpty().When(x => x.Descriptor?.Scope is not null);
        RuleFor(x => x.VaultId).NotEmpty().When(x => x.Descriptor?.Scope is not null);
        RuleFor(x => x.DiscoveryKeyRevision).NotEmpty().MaximumLength(20)
            .When(x => x.Descriptor is not null);
        RuleFor(x => x.VdkVersion).GreaterThan(0u).When(x => x.Descriptor is not null);
        RuleFor(x => x.MemberKeyGeneration).GreaterThan(0u)
            .When(x => x.Descriptor?.MemberKeyGeneration is not null);
        RuleFor(x => x.WrappingKeyVersion).GreaterThan(0u).When(x => x.Descriptor?.Binding is not null);
        RuleFor(x => x.EncodedSuitePayload).NotEmpty().MaximumLength(374);
    }
}

internal sealed class VaultPrivateKeyContractValidator : AbstractValidator<VaultPrivateKeyEnvelopeContract>
{
    internal VaultPrivateKeyContractValidator()
    {
        RuleFor(x => x.Descriptor).NotNull();
        RuleFor(x => x.Descriptor!.Scope).NotNull().SetValidator(new EnvelopeScopeContractValidator()!)
            .When(x => x.Descriptor is not null);
        RuleFor(x => x.Descriptor!.Binding).NotNull().When(x => x.Descriptor is not null);
        RuleFor(x => x.Descriptor!.MemberKeyGeneration).NotNull().When(x => x.Descriptor is not null);
        RuleFor(x => x.OrganizationId).NotEmpty().When(x => x.Descriptor?.Scope is not null);
        RuleFor(x => x.VaultId).NotEmpty().When(x => x.Descriptor?.Scope is not null);
        RuleFor(x => x.PrivateKeyKind).InclusiveBetween((ushort)1, (ushort)2)
            .When(x => x.Descriptor is not null);
        RuleFor(x => x.PrivateKeyRevision).NotEmpty().MaximumLength(20)
            .When(x => x.Descriptor is not null);
        RuleFor(x => x.PrivateKeyVersion).GreaterThan(0u).When(x => x.Descriptor is not null);
        RuleFor(x => x.MemberKeyGeneration).GreaterThan(0u)
            .When(x => x.Descriptor?.MemberKeyGeneration is not null);
        RuleFor(x => x.WrappingKeyVersion).GreaterThan(0u).When(x => x.Descriptor?.Binding is not null);
        RuleFor(x => x.EncodedSuitePayload).NotEmpty().MaximumLength(374);
    }
}

internal sealed class MemberVaultMetadataContractValidator : AbstractValidator<MemberVaultMetadataEnvelopeContract>
{
    internal MemberVaultMetadataContractValidator()
    {
        RuleFor(x => x.Descriptor).NotNull();
        RuleFor(x => x.Descriptor!.Scope).NotNull().SetValidator(new EnvelopeScopeContractValidator()!)
            .When(x => x.Descriptor is not null);
        RuleFor(x => x.OrganizationId).NotEmpty().When(x => x.Descriptor?.Scope is not null);
        RuleFor(x => x.VaultId).NotEmpty().When(x => x.Descriptor?.Scope is not null);
        RuleFor(x => x.MetadataRevision).NotEmpty().MaximumLength(20).When(x => x.Descriptor is not null);
        RuleFor(x => x.EncodedSuitePayload).NotEmpty().MaximumLength(21_878);
    }
}

internal sealed class MemberVaultKeyContractValidator : AbstractValidator<MemberVaultKeyEnvelopeContract>
{
    internal MemberVaultKeyContractValidator()
    {
        RuleFor(x => x.WrappedVaultKey).NotNull();
        RuleFor(x => x.WrappedVaultKey!.Descriptor).NotNull()
            .When(x => x.WrappedVaultKey is not null);
        RuleFor(x => x.WrappedVaultKey!.Descriptor.Scope).NotNull()
            .SetValidator(new EnvelopeScopeContractValidator()!)
            .When(x => x.WrappedVaultKey?.Descriptor is not null);
        RuleFor(x => x.OrganizationId).NotEmpty()
            .When(x => x.WrappedVaultKey?.Descriptor?.Scope is not null);
        RuleFor(x => x.VaultId).NotEmpty()
            .When(x => x.WrappedVaultKey?.Descriptor?.Scope is not null);
        RuleFor(x => x.MemberId).NotEmpty()
            .When(x => x.WrappedVaultKey?.Descriptor?.Scope?.MemberId is not null);
        RuleFor(x => x.RecipientMemberKeyFingerprint).NotEmpty().Length(43)
            .When(x => x.WrappedVaultKey?.Descriptor is not null);
        RuleFor(x => x.WrapperSuiteId).Equal(X25519SealedBoxContract.SuiteId)
            .When(x => x.WrappedVaultKey?.Descriptor is not null);
        RuleFor(x => x.SealedVaultKeyPackage).NotEmpty().Length(160)
            .When(x => x.WrappedVaultKey is not null);
    }
}

[PublicAPI]
internal sealed class CreateVaultEndpoint(
    VaultDomainWriteContext domainWriteContext,
    IClock clock) : Endpoint<CreateVaultRequest, CreateVaultResponse>
{
    public override void Configure()
    {
        Post("api/vaults");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.VaultCreate);
        this.RequireEmailVerified();
        Summary(summary =>
        {
            summary.Summary = "Create a new encrypted Vault";
            summary.Description = "Consumes a server-issued creation challenge and stores only authenticated ciphertext plus structural key metadata. Vault names and descriptions are never sent in plaintext.";
        });
        Tags("Vault/Vaults");
    }

    public override async Task HandleAsync(CreateVaultRequest req, CancellationToken ct)
    {
        Domain.Vault vault;
        try
        {
            vault = await CreateAsync(req, false, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            domainWriteContext.Clear();
            ThrowError("Vault creation state changed concurrently. Retry with a fresh challenge if required.");
            return;
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException
        {
            SqlState: Palladin.Core.Persistence.PostgresErrorCodes.UniqueViolation,
            ConstraintName: "PK_VaultOrganizationLifecycles",
        })
        {
            domainWriteContext.Clear();
            ThrowError("Vault organization lifecycle changed concurrently. Retry the request.");
            return;
        }

        await Send.CreatedAtAsync<GetVaultEndpoint>(
            new { id = vault.Id },
            ToResponse(vault, vault.VaultMemberKeyEnvelopes.Single()),
            cancellation: ct);
    }

    internal async Task<Domain.Vault> CreateAsync(CreateVaultRequest req, bool isDefault, CancellationToken ct)
    {
        var userId = User.GetUserId()!.Value;
        var organizationId = User.GetOrganizationId()!.Value;
        var now = clock.GetCurrentInstant();
        await FenceOrganizationLifecycleAsync(domainWriteContext, organizationId, ct);
        await EnsureMemberIsNotRemovingAsync(domainWriteContext, organizationId, userId, ct);

        var challenge = await domainWriteContext.VaultCreationChallenges
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.VaultId == req.VaultId, ct);
        if (challenge is null)
        {
            ThrowError("A valid server-issued Vault creation challenge is required.");
        }

        challenge!.Consume(organizationId, userId, now);
        var metadata = VaultEnvelopeContractMapper.ToDomain(req.MemberVaultMetadata);
        var creatorVaultKey = VaultEnvelopeContractMapper.ToDomain(req.CreatorVaultKey);
        var keyEpoch = VaultEnvelopeContractMapper.ToDomain(req.CurrentKeyEpoch);
        var keyMaterial = new[] { VaultEnvelopeContractMapper.ToDomain(req.DiscoveryKey) }
            .Concat(req.VaultPrivateKeys.Select(VaultEnvelopeContractMapper.ToDomain))
            .ToArray();
        var agentMessagePublicKey = VaultEnvelopeContractMapper.ToDomain(req.VaultAgentMessagePublicKey,
            VaultPublicKeyKindContract.AgentMessageX25519, req.CurrentKeyEpoch.AgentMessageKeyVersion);
        var manifestSigningPublicKey = VaultEnvelopeContractMapper.ToDomain(req.VaultManifestSigningPublicKey,
            VaultPublicKeyKindContract.ManifestSigningEd25519, req.CurrentKeyEpoch.ManifestSigningKeyVersion);
        var memberKeyGeneration = new MemberKeyGeneration(req.CreatorVaultKey.MemberKeyGeneration);
        var memberKeyDirectory = await domainWriteContext.MemberKeyDirectory
            .SingleOrDefaultAsync(x => x.UserId == userId, ct);
        ValidateCreatorKeyDirectory(memberKeyDirectory, creatorVaultKey);

        var vault = isDefault
            ? Domain.Vault.CreateDefault(req.VaultId, organizationId, userId, User.GetDisplayName(), metadata,
                memberKeyGeneration, keyEpoch, creatorVaultKey, keyMaterial, agentMessagePublicKey,
                manifestSigningPublicKey, now)
            : Domain.Vault.Create(req.VaultId, organizationId, userId, User.GetDisplayName(), metadata,
                memberKeyGeneration, keyEpoch, creatorVaultKey, keyMaterial, agentMessagePublicKey,
                manifestSigningPublicKey, now);

        domainWriteContext.Add(vault);
        await domainWriteContext.CommitAsync(ct);
        return vault;
    }

    internal static async Task FenceOrganizationLifecycleAsync(
        VaultDomainWriteContext domainWriteContext,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        var lifecycle = await domainWriteContext.VaultOrganizationLifecycles
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId, cancellationToken);
        if (lifecycle is null)
        {
            domainWriteContext.Add(VaultOrganizationLifecycle.Create(organizationId));
            return;
        }

        lifecycle.FenceMutation();
    }

    internal static async Task EnsureMemberIsNotRemovingAsync(
        VaultDomainWriteContext domainWriteContext,
        Guid organizationId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        if (await domainWriteContext.VaultPrincipalDeprovisionings.AnyAsync(x =>
                x.OrganizationId == organizationId
                && x.PrincipalType == VaultPrincipalType.OrganizationMember
                && x.PrincipalId == userId
                && x.Status != VaultPrincipalDeprovisioningStatus.Completed,
                cancellationToken))
        {
            throw new MemberDeprovisioningInProgressException();
        }
    }

    internal static void ValidateCreatorKeyDirectory(
        MemberKeyDirectoryEntry? directory,
        MemberWrappedVaultKey creatorVaultKey)
    {
        if (directory is null || !directory.Matches(creatorVaultKey))
        {
            throw new DomainException(
                "The creator Vault key envelope recipient does not match the authenticated Member key directory.");
        }
    }

    internal static CreateVaultResponse ToResponse(Domain.Vault vault, VaultMemberKeyEnvelope memberKey) => new(
        vault.Id,
        vault.OrganizationId,
        vault.IsDefault,
        vault.ProtocolVersion,
        vault.MemberSequence.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        vault.DiscoverySequence.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        vault.MemberKeyGeneration.Value,
        VaultEnvelopeContractMapper.ToContract(vault.CurrentKeyEpoch),
        VaultEnvelopeContractMapper.ToContract(vault.GetMemberVaultMetadata()),
        VaultEnvelopeContractMapper.ToContract(memberKey.GetWrappedVaultKey()),
        VaultEnvelopeContractMapper.ToDiscoveryKeyContract(
            vault.KeyMaterialEnvelopes.Single(x => x.Kind == VaultKeyMaterialKind.DiscoveryKey)),
        vault.KeyMaterialEnvelopes
            .Where(x => x.Kind != VaultKeyMaterialKind.DiscoveryKey)
            .OrderBy(x => x.Kind)
            .Select(VaultEnvelopeContractMapper.ToPrivateKeyContract)
            .ToArray(),
        VaultEnvelopeContractMapper.ToAgentMessagePublicKeyContract(vault),
        VaultEnvelopeContractMapper.ToManifestSigningPublicKeyContract(vault),
        vault.CreatedAt,
        vault.UpdatedAt);
}

internal sealed class MemberDeprovisioningInProgressException()
    : ConflictException("Organization Member removal is in progress.");

internal sealed class VaultOrganizationLifecycleChangedException()
    : ConflictException("Vault organization lifecycle changed concurrently. Retry the request.");
