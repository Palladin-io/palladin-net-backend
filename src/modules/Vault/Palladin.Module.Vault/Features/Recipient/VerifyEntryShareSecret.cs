using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using Palladin.Core.Types;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Sharing;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record VerifyEntryShareSecretRequest
{
    public Guid ShareId { get; init; }
    public Guid SessionId { get; init; }
    public string SessionToken { get; init; } = string.Empty;
    public string Secret { get; init; } = string.Empty;
    public override string ToString() => nameof(VerifyEntryShareSecretRequest);
}

[UsedImplicitly]
internal sealed class VerifyEntryShareSecretValidator : Validator<VerifyEntryShareSecretRequest>
{
    public VerifyEntryShareSecretValidator()
    {
        RuleFor(x => x.ShareId).NotEmpty();
        RuleFor(x => x.SessionId).NotEmpty();
        RuleFor(x => x.SessionToken).NotEmpty().Length(43);
        RuleFor(x => x.Secret).NotEmpty().MaximumLength(256);
    }
}

[PublicAPI]
internal sealed class VerifyEntryShareSecretEndpoint(
    VaultDomainWriteContext context, EntryShareReceiver receiver, EntryShareSecurity security,
    IOptions<EntrySharingOptions> options, IClock clock) : Endpoint<VerifyEntryShareSecretRequest>
{
    public override void Configure()
    {
        Post("api/entry-shares/{shareId:guid}/sessions/{sessionId:guid}/verify-secret");
        AllowAnonymous();
        Tags("Vault/Sharing");
        Summary(s =>
        {
            s.Summary = "Verify the optional sharing password or PIN";
            s.Description = "The guest session bearer authenticates this exchange. Verification and failed-attempt "
                + "budgets are bound to current share policy; verifying this gate never satisfies a required email gate.";
        });
        Options(builder => builder.WithMetadata(new RequestSizeLimitAttribute(4096)));
    }

    public override async Task HandleAsync(VerifyEntryShareSecretRequest req, CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        try
        {
            var (share, session) = await receiver.LoadSessionAsync(req.ShareId, req.SessionId, req.SessionToken, ct);
            var now = clock.GetCurrentInstant();
            if (share.Protection == EntryShareProtection.None)
            {
                throw new EntryShareUnavailableException();
            }

            if (!security.VerifySecret(share.Id, share.Protection, req.Secret, share.SecretVerifier))
            {
                share.RegisterFailedAttempt(now, options.Value.FailedAttemptLimit,
                    options.Value.TotalFailedAttemptLimit, Duration.FromSeconds(options.Value.LockoutSeconds));
                await context.CommitAsync(ct);
                await Send.NotFoundAsync(ct);
                return;
            }

            share.VerifySecret(session, now);
            await context.CommitAsync(ct);
            await Send.NoContentAsync(ct);
        }
        catch (EntryShareUnavailableException)
        {
            await Send.NotFoundAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await Send.NotFoundAsync(ct);
        }
    }
}
