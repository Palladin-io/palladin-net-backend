using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Palladin.Core.Api;
using Palladin.Core.Cryptography;
using Palladin.Core.Security;
using Palladin.Module.Identity.Contracts.Commands;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record DeleteLegacyMemberKeyRequest
{
    [QueryParam] public uint LegacyMemberKeyVersion { get; init; }
}

[UsedImplicitly]
internal sealed class DeleteLegacyMemberKeyValidator : Validator<DeleteLegacyMemberKeyRequest>
{
    public DeleteLegacyMemberKeyValidator() => RuleFor(x => x.LegacyMemberKeyVersion).GreaterThan(0u);
}

// The wrapped legacy key lives in Identity, but only Vault knows which envelopes still need it, so Vault is the
// authority for this deletion and hands the write to Identity as a command.
[PublicAPI]
internal sealed class DeleteLegacyMemberKeyEndpoint(VaultDomainReadContext domainReadContext, IPublishEndpoint publishEndpoint)
    : Endpoint<DeleteLegacyMemberKeyRequest>
{
    public override void Configure()
    {
        Delete("api/account/legacy-member-key");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Options(builder => builder.AllowNonActiveOrganizationMembership());
        Summary(summary =>
        {
            summary.Summary = "Delete the wrapped legacy Member private key";
            summary.Description = "Accepts the deletion (202) only when the key directory already holds a newer Member "
                + "key on the current key-wrapper suite and no current Vault-key envelope in any organization is "
                + "addressed to another key; otherwise 409 member-key-directory-not-current or legacy-member-key-in-use. "
                + "Identity then deletes the wrapped legacy key if its version still equals legacyMemberKeyVersion; "
                + "GET api/account shows legacyMemberKey null afterwards.";
        });
        Tags("Vault/KeyMigration");
    }

    public override async Task HandleAsync(DeleteLegacyMemberKeyRequest req, CancellationToken ct)
    {
        var userId = User.GetUserId()!.Value;
        var directory = await domainReadContext.MemberKeyDirectory
            .Where(x => x.UserId == userId)
            .Select(x => new { x.KeyVersion, x.PublicKeySuiteId })
            .SingleOrDefaultAsync(ct);
        // Once the directory holds the newer current-suite key, no new envelope can address the legacy key.
        if (directory is null
            || directory.KeyVersion.Value <= req.LegacyMemberKeyVersion
            || directory.PublicKeySuiteId != KeyWrapperSuites.Current.Id)
        {
            await SendConflictAsync("member-key-directory-not-current", ct);
            return;
        }

        var envelopes = MemberKeyMigration.CurrentEnvelopes(domainReadContext, userId);
        if (await MemberKeyMigration.PendingRewrap(envelopes, directory.KeyVersion, directory.PublicKeySuiteId).AnyAsync(ct))
        {
            await SendConflictAsync("legacy-member-key-in-use", ct);
            return;
        }

        await publishEndpoint.Publish(new DeleteLegacyMemberKeyCommand(userId, req.LegacyMemberKeyVersion), ct);
        await Send.ResultAsync(Results.Accepted());
    }

    private Task SendConflictAsync(string code, CancellationToken ct)
    {
        AddError(ErrorResponses.General(code));
        return Send.ErrorsAsync(StatusCodes.Status409Conflict, ct);
    }
}
