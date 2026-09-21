using System.Globalization;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Core.Types.Exceptions;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Sharing;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record CreateEntryShareRequest
{
    public Guid VaultId { get; init; }
    public Guid EntryId { get; init; }
    public Guid ShareId { get; init; }
    public string SourceRevision { get; init; } = string.Empty;
    public Instant ExpiresAt { get; init; }
    public int MaximumReceipts { get; init; } = 1;
    public EntryShareRecipientMode RecipientMode { get; init; } = EntryShareRecipientMode.NamedRecipient;
    public string? RecipientEmail { get; init; }
    public EntryShareProtection Protection { get; init; }
    public string? ProtectionSecret { get; init; }
    public string AccessToken { get; init; } = string.Empty;
    public byte[] Nonce { get; init; } = [];
    public byte[] Ciphertext { get; init; } = [];
    public bool NotifyOnFirstReceipt { get; init; }
    public override string ToString() => nameof(CreateEntryShareRequest);
}

[PublicAPI]
public sealed record CreateEntryShareResponse(Guid ShareId, Instant ExpiresAt, int MaximumReceipts);

[UsedImplicitly]
internal sealed class CreateEntryShareValidator : Validator<CreateEntryShareRequest>
{
    public CreateEntryShareValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.EntryId).NotEmpty();
        RuleFor(x => x.ShareId).NotEmpty();
        RuleFor(x => x.SourceRevision).NotEmpty().MaximumLength(20);
        RuleFor(x => x.RecipientMode).IsInEnum();
        RuleFor(x => x.Protection).IsInEnum();
        RuleFor(x => x.AccessToken).NotEmpty().Length(43);
        RuleFor(x => x.Nonce).NotNull().Must(x => x is { Length: EntryShare.NonceBytes });
        RuleFor(x => x.Ciphertext).NotNull().Must(x => x is { Length: >= 16 and <= EntryShare.MaximumCiphertextBytes });
        RuleFor(x => x.RecipientEmail).NotEmpty().MaximumLength(320).EmailAddress()
            .When(x => x.RecipientMode == EntryShareRecipientMode.NamedRecipient);
        RuleFor(x => x.RecipientEmail).Null().When(x => x.RecipientMode == EntryShareRecipientMode.AnyoneWithLink);
        RuleFor(x => x.ProtectionSecret).MaximumLength(256);
    }
}

[PublicAPI]
internal sealed class CreateEntryShareEndpoint(
    VaultDomainWriteContext context,
    EntryShareAuthority authority,
    EntryShareSecurity security,
    IOptions<EntrySharingOptions> options,
    IClock clock) : Endpoint<CreateEntryShareRequest, CreateEntryShareResponse>
{
    public override void Configure()
    {
        Post("api/vaults/{vaultId:guid}/entries/{entryId:guid}/sharing");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.VaultManage);
        this.RequireEmailVerified();
        Tags("Vault/Sharing");
        Summary(s => s.Summary = "Create an independently encrypted sharing snapshot with finite access");
    }

    public override async Task HandleAsync(CreateEntryShareRequest req, CancellationToken ct)
   {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var scope = new EntryScope(User.GetOrganizationId()!.Value, req.VaultId, req.EntryId);
        var senderId = User.GetUserId()!.Value;
        var authorizationVersion = User.GetAuthorizationVersion()!.Value;
        var source = await authority.LoadSenderSourceAsync(scope, senderId, authorizationVersion, ct);
        var existing = await context.EntryShares.SingleOrDefaultAsync(x => x.Id == req.ShareId, ct);
        if (existing is not null)
        {
            if (!IsExactRetry(existing, req, scope, senderId))
            {
                throw new EntryShareUnavailableException();
            }

            await context.CommitAsync(ct);
            await Send.OkAsync(new CreateEntryShareResponse(existing.Id, existing.ExpiresAt, existing.MaximumReceipts), ct);
            return;
        }

        var now = clock.GetCurrentInstant();
        if (req.ExpiresAt <= now || req.ExpiresAt > now + Duration.FromHours(options.Value.MaximumLifetimeHours)
            || req.MaximumReceipts < 1 || req.MaximumReceipts > options.Value.MaximumReceipts
            || req.SourceRevision != source.Entry.CurrentRevision.Value.ToString(CultureInfo.InvariantCulture))
        {
            throw new DomainException("The sharing limits or source revision are invalid.");
        }

        var activeCount = await context.EntryShares.CountAsync(x => x.OrganizationId == scope.OrganizationId
            && x.VaultId == scope.VaultId && x.EntryId == scope.EntryId
            && x.RevokedAt == null && x.ExpiresAt > now, ct);
        if (activeCount >= options.Value.MaximumActiveLinksPerEntry)
        {
            throw new DomainException("The active sharing link limit has been reached.");
        }

        var challenge = await context.EntryShareCreationChallenges.SingleOrDefaultAsync(
            x => x.OrganizationId == scope.OrganizationId && x.VaultId == scope.VaultId
                 && x.EntryId == scope.EntryId && x.RequestedBy == senderId, ct);
        if (challenge is null)
        {
            throw new EntryShareUnavailableException();
        }

        challenge.Validate(req.ShareId, scope, senderId, now);
        var share = EntryShare.Create(req.ShareId, scope, source.Entry.CurrentRevision, senderId, now,
            req.ExpiresAt, req.MaximumReceipts, req.RecipientMode,
            req.RecipientEmail is null ? null : security.ProtectRecipientEmail(req.ShareId, NormalizeEmail(req.RecipientEmail)),
            req.Protection, security.CreateSecretVerifier(req.ShareId, req.Protection, req.ProtectionSecret),
            security.HashAccessToken(req.ShareId, req.AccessToken), req.Nonce, req.Ciphertext,
            req.NotifyOnFirstReceipt, authorizationVersion, source.Member.AddedAt);
        context.Add(share);
        context.Remove(challenge);
        source.Vault.FenceAccessMutation(senderId, now);
        await context.CommitAsync(ct);
        await Send.OkAsync(new CreateEntryShareResponse(share.Id, share.ExpiresAt, share.MaximumReceipts), ct);
    }

    private bool IsExactRetry(EntryShare share, CreateEntryShareRequest req, EntryScope scope, Guid senderId) =>
        share.OrganizationId == scope.OrganizationId && share.VaultId == scope.VaultId && share.EntryId == scope.EntryId
        && share.CreatedBy == senderId && share.SourceRevision.Value.ToString(CultureInfo.InvariantCulture) == req.SourceRevision
        && share.ExpiresAt == req.ExpiresAt && share.MaximumReceipts == req.MaximumReceipts
        && share.RecipientMode == req.RecipientMode && share.Protection == req.Protection
        && share.NotifyOnFirstReceipt == req.NotifyOnFirstReceipt
        && share.Nonce.AsSpan().SequenceEqual(req.Nonce) && share.Ciphertext.AsSpan().SequenceEqual(req.Ciphertext)
        && security.VerifyAccessToken(share.Id, req.AccessToken, share.AccessTokenHash)
        && (share.Protection == EntryShareProtection.None
            ? req.ProtectionSecret is null
            : security.VerifySecret(share.Id, share.Protection, req.ProtectionSecret, share.SecretVerifier))
        && (share.ProtectedRecipientEmail is null
            ? req.RecipientEmail is null
            : req.RecipientEmail is not null
              && security.UnprotectRecipientEmail(share.Id, share.ProtectedRecipientEmail) == NormalizeEmail(req.RecipientEmail));

    internal static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}
