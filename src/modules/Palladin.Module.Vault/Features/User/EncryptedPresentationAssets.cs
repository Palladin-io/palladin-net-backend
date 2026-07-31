using System.Security.Cryptography;
using System.Text.Json.Serialization;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Guid;
using Palladin.Core.Security;
using Palladin.Core.Json;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Authorization;
using Palladin.Module.Vault.Infrastructure.Assets;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public enum PresentationAssetTargetContract
{
    Vault = 1,
    Entry = 2,
}

[PublicAPI]
public sealed record UploadEncryptedPresentationAssetRequest : IRequiresVaultMembership
{
    public Guid VaultId { get; init; }
    public Guid AssetId { get; init; }
    public PresentationAssetTargetContract Target { get; init; }
    public Guid? EntryId { get; init; }
    public string MediaType { get; init; } = string.Empty;

    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))]
    public byte[] Ciphertext { get; init; } = [];

    public string CiphertextSha256 { get; init; } = string.Empty;
}

[PublicAPI]
public sealed record EncryptedPresentationAssetResponse(
    Guid AssetId,
    PresentationAssetTargetContract Target,
    Guid? EntryId,
    string MediaType,
    int CiphertextLength,
    string CiphertextSha256,
    Instant CreatedAt);

[PublicAPI]
public sealed record GetEncryptedPresentationAssetRequest : IRequiresVaultMembership
{
    public Guid VaultId { get; init; }
    public Guid AssetId { get; init; }
}

[PublicAPI]
public sealed record GetEncryptedPresentationAssetResponse(
    Guid AssetId,
    PresentationAssetTargetContract Target,
    Guid? EntryId,
    string MediaType,
    int CiphertextLength,
    string CiphertextSha256,
    string DownloadUrl);

[PublicAPI]
public sealed record DeleteEncryptedPresentationAssetRequest : IRequiresVaultMembership
{
    public Guid VaultId { get; init; }
    public Guid AssetId { get; init; }
}

[UsedImplicitly]
internal sealed class UploadEncryptedPresentationAssetValidator
    : Validator<UploadEncryptedPresentationAssetRequest>
{
    internal const long MaximumRequestBytes = 7 * 1024 * 1024;

    public UploadEncryptedPresentationAssetValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.AssetId).NotEmpty();
        RuleFor(x => x.Target).IsInEnum();
        RuleFor(x => x.EntryId).NotEmpty().When(x => x.Target == PresentationAssetTargetContract.Entry);
        RuleFor(x => x.EntryId).Null().When(x => x.Target == PresentationAssetTargetContract.Vault);
        RuleFor(x => x.MediaType)
            .Must(PresentationAssetMediaTypes.IsAllowed)
            .WithMessage("MediaType must be one of: image/jpeg, image/png, image/webp.");
        RuleFor(x => x.Ciphertext)
            .NotEmpty()
            .Must(value => value.Length <= EncryptedPresentationAsset.MaximumCiphertextBytes)
            .WithMessage($"Ciphertext cannot exceed {EncryptedPresentationAsset.MaximumCiphertextBytes} bytes.");
        RuleFor(x => x.CiphertextSha256)
            .Must(IsCanonicalSha256)
            .WithMessage("CiphertextSha256 must be a canonical base64url SHA-256 digest.");
    }

    private static bool IsCanonicalSha256(string value)
    {
        try
        {
            var decoded = WebEncoders.Base64UrlDecode(value);
            return decoded.Length == EncryptedPresentationAsset.DigestLength
                   && WebEncoders.Base64UrlEncode(decoded) == value;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

[UsedImplicitly]
internal sealed class GetEncryptedPresentationAssetValidator
    : Validator<GetEncryptedPresentationAssetRequest>
{
    public GetEncryptedPresentationAssetValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.AssetId).NotEmpty();
    }
}

[UsedImplicitly]
internal sealed class DeleteEncryptedPresentationAssetValidator
    : Validator<DeleteEncryptedPresentationAssetRequest>
{
    public DeleteEncryptedPresentationAssetValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.AssetId).NotEmpty();
    }
}

[PublicAPI]
internal sealed class UploadEncryptedPresentationAssetEndpoint(
    VaultDomainWriteContext domainWriteContext,
    IEncryptedPresentationAssetStorage assetStorage,
    IGuidProvider guidProvider,
    IClock clock) : Endpoint<UploadEncryptedPresentationAssetRequest, EncryptedPresentationAssetResponse>
{
    public override void Configure()
    {
        Post("api/vaults/{vaultId:guid}/assets");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.VaultManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Options(builder => builder.WithMetadata(
            new RequestSizeLimitAttribute(UploadEncryptedPresentationAssetValidator.MaximumRequestBytes)));
        Summary(summary =>
        {
            summary.Summary = "Upload an encrypted presentation asset";
            summary.Description = "Stores an opaque, client-encrypted image in private object storage. The backend validates only bounded ciphertext metadata and never inspects image bytes or receives a domain or URL.";
        });
        Tags("Vault/Assets");
    }

    public override async Task HandleAsync(UploadEncryptedPresentationAssetRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId()!.Value;
        var userId = User.GetUserId()!.Value;
        var target = (PresentationAssetTarget)req.Target;
        var suppliedDigest = WebEncoders.Base64UrlDecode(req.CiphertextSha256);
        var computedDigest = SHA256.HashData(req.Ciphertext);
        if (!CryptographicOperations.FixedTimeEquals(suppliedDigest, computedDigest))
        {
            AddError(r => r.CiphertextSha256, "Ciphertext digest does not match the uploaded bytes.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        await using var registrationTransaction = await domainWriteContext.BeginTransactionAsync(ct);
        var vaultExists = await domainWriteContext.LockVault(organizationId, req.VaultId).AnyAsync(ct);
        var membershipExists = vaultExists && await domainWriteContext.VaultMembers.AnyAsync(
            x => x.OrganizationId == organizationId
                 && x.VaultId == req.VaultId
                 && x.UserId == userId,
            ct);
        if (!membershipExists)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var asset = await domainWriteContext.EncryptedPresentationAssets.SingleOrDefaultAsync(
            x => x.OrganizationId == organizationId && x.VaultId == req.VaultId && x.Id == req.AssetId,
            ct);
        var created = false;
        if (asset is not null)
        {
            if (!asset.IsExactRetry(target, req.EntryId, req.MediaType, req.Ciphertext.Length, computedDigest))
            {
                ThrowError("Asset identifier is already bound to different ciphertext.");
            }
        }
        else
        {
            EntryScope? entryScope = null;
            if (target == PresentationAssetTarget.Entry)
            {
                var entryExists = await domainWriteContext.Entries.AnyAsync(
                    x => x.OrganizationId == organizationId
                         && x.VaultId == req.VaultId
                         && x.Id == req.EntryId,
                    ct);
                if (!entryExists)
                {
                    await Send.NotFoundAsync(ct);
                    return;
                }

                entryScope = new EntryScope(organizationId, req.VaultId, req.EntryId!.Value);
            }

            asset = EncryptedPresentationAsset.Create(
                entryScope,
                organizationId,
                req.VaultId,
                req.AssetId,
                target,
                guidProvider.Generate(),
                req.MediaType,
                req.Ciphertext.Length,
                computedDigest,
                userId,
                clock.GetCurrentInstant());
            domainWriteContext.Add(asset);
            created = true;
        }

        await domainWriteContext.CommitAsync(registrationTransaction, ct);
        var storageKey = asset.StorageKey;
        var objectExists = await assetStorage.ExistsAsync(storageKey, ct);
        if (asset.Status != EncryptedPresentationAssetStatus.Ready || !objectExists)
        {
            if (!objectExists)
            {
                await UploadCiphertextAsync(assetStorage, storageKey, req.Ciphertext, ct);
            }

            domainWriteContext.Clear();
            var canFinalize = false;
            await using (var finalizationTransaction = await domainWriteContext.BeginTransactionAsync(ct))
            {
                vaultExists = await domainWriteContext.LockVault(organizationId, req.VaultId).AnyAsync(ct);
                membershipExists = vaultExists && await domainWriteContext.VaultMembers.AnyAsync(
                    x => x.OrganizationId == organizationId
                         && x.VaultId == req.VaultId
                         && x.UserId == userId,
                    ct);
                asset = membershipExists
                    ? await domainWriteContext.EncryptedPresentationAssets.SingleOrDefaultAsync(
                        x => x.OrganizationId == organizationId
                             && x.VaultId == req.VaultId
                             && x.Id == req.AssetId,
                        ct)
                    : null;
                canFinalize = asset is not null
                              && asset.IsExactRetry(
                                  target,
                                  req.EntryId,
                                  req.MediaType,
                                  req.Ciphertext.Length,
                                  computedDigest);
                if (canFinalize)
                {
                    asset!.MarkReady(clock.GetCurrentInstant());
                    await domainWriteContext.CommitAsync(finalizationTransaction, ct);
                }
            }

            if (!canFinalize)
            {
                await assetStorage.DeleteAllVersionsAsync(storageKey, ct);
                await Send.NotFoundAsync(ct);
                return;
            }
        }

        if (created)
        {
            await Send.CreatedAtAsync<GetEncryptedPresentationAssetEndpoint>(
                new { vaultId = req.VaultId, assetId = req.AssetId },
                ToResponse(asset!),
                cancellation: ct);
        }
        else
        {
            await Send.OkAsync(ToResponse(asset!), ct);
        }
    }

    private static async Task UploadCiphertextAsync(
        IEncryptedPresentationAssetStorage storage,
        string storageKey,
        byte[] ciphertext,
        CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream(ciphertext, writable: false);
        await storage.PutAsync(stream, storageKey, cancellationToken);
    }

    private static EncryptedPresentationAssetResponse ToResponse(EncryptedPresentationAsset asset) => new(
        asset.Id,
        (PresentationAssetTargetContract)asset.Target,
        asset.EntryId,
        asset.MediaType,
        asset.CiphertextLength,
        WebEncoders.Base64UrlEncode(asset.CiphertextSha256),
        asset.CreatedAt);
}

[PublicAPI]
internal sealed class GetEncryptedPresentationAssetEndpoint(
    VaultDomainReadContext domainReadContext,
    IEncryptedPresentationAssetStorage assetStorage) : Endpoint<GetEncryptedPresentationAssetRequest, GetEncryptedPresentationAssetResponse>
{
    public override void Configure()
    {
        Get("api/vaults/{vaultId:guid}/assets/{assetId:guid}");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.VaultManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(summary =>
        {
            summary.Summary = "Get an encrypted presentation asset";
            summary.Description = "Returns a short-lived private download URL and integrity metadata for opaque ciphertext. Caller must be a Member of the owning Vault.";
        });
        Tags("Vault/Assets");
    }

    public override async Task HandleAsync(GetEncryptedPresentationAssetRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId()!.Value;
        var asset = await domainReadContext.EncryptedPresentationAssets.SingleOrDefaultAsync(
            x => x.OrganizationId == organizationId && x.VaultId == req.VaultId && x.Id == req.AssetId,
            ct);
        if (asset is null || asset.Status != EncryptedPresentationAssetStatus.Ready)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var downloadUrl = await assetStorage.CreateDownloadUrlAsync(asset.StorageKey, ct);
        await Send.OkAsync(new GetEncryptedPresentationAssetResponse(
            asset.Id,
            (PresentationAssetTargetContract)asset.Target,
            asset.EntryId,
            asset.MediaType,
            asset.CiphertextLength,
            WebEncoders.Base64UrlEncode(asset.CiphertextSha256),
            downloadUrl), ct);
    }
}

[PublicAPI]
internal sealed class DeleteEncryptedPresentationAssetEndpoint(
    VaultDomainWriteContext domainWriteContext,
    IEncryptedPresentationAssetStorage assetStorage) : Endpoint<DeleteEncryptedPresentationAssetRequest>
{
    public override void Configure()
    {
        Delete("api/vaults/{vaultId:guid}/assets/{assetId:guid}");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.VaultManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(summary =>
        {
            summary.Summary = "Delete an encrypted presentation asset";
            summary.Description = "Deletes every retained object version and its tenant-scoped metadata. The operation never logs or resolves presentation content.";
        });
        Tags("Vault/Assets");
    }

    public override async Task HandleAsync(DeleteEncryptedPresentationAssetRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId()!.Value;
        var userId = User.GetUserId()!.Value;
        await using var transaction = await domainWriteContext.BeginTransactionAsync(ct);
        var vaultExists = await domainWriteContext.LockVault(organizationId, req.VaultId).AnyAsync(ct);
        var membershipExists = vaultExists && await domainWriteContext.VaultMembers.AnyAsync(
            x => x.OrganizationId == organizationId
                 && x.VaultId == req.VaultId
                 && x.UserId == userId,
            ct);
        if (!membershipExists)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var asset = await domainWriteContext.EncryptedPresentationAssets.SingleOrDefaultAsync(
            x => x.OrganizationId == organizationId && x.VaultId == req.VaultId && x.Id == req.AssetId,
            ct);
        if (asset is null)
        {
            await Send.NoContentAsync(ct);
            return;
        }

        await assetStorage.DeleteAllVersionsAsync(asset.StorageKey, ct);
        domainWriteContext.Remove(asset);
        await domainWriteContext.CommitAsync(transaction, ct);
        await Send.NoContentAsync(ct);
    }
}
