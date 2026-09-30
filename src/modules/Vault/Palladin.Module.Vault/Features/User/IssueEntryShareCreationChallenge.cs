using System.Globalization;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using Palladin.Core.Guid;
using Palladin.Core.Security;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Creation;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Sharing;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record IssueEntryShareCreationChallengeRequest(Guid VaultId, Guid EntryId);

[PublicAPI]
public sealed record IssueEntryShareCreationChallengeResponse(Guid ShareId, string SourceRevision, Instant ExpiresAt);

[UsedImplicitly]
internal sealed class IssueEntryShareCreationChallengeValidator : Validator<IssueEntryShareCreationChallengeRequest>
{
    public IssueEntryShareCreationChallengeValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.EntryId).NotEmpty();
    }
}

[PublicAPI]
internal sealed class IssueEntryShareCreationChallengeEndpoint(
    VaultDomainWriteContext context,
    EntryShareAuthority authority,
    IGuidProvider guidProvider,
    IClock clock,
    IOptions<VaultCreationOptions> options)
    : Endpoint<IssueEntryShareCreationChallengeRequest, IssueEntryShareCreationChallengeResponse>
{
    public override void Configure()
    {
        Post("api/vaults/{vaultId:guid}/entries/{entryId:guid}/sharing/creation-challenge");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.VaultManage);
        this.RequireEmailVerified();
        Tags("Vault/Sharing");
        Summary(s => s.Summary = "Reserve the scope for a client-encrypted Entry sharing snapshot");
    }

    public override async Task HandleAsync(IssueEntryShareCreationChallengeRequest req, CancellationToken ct)
   {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var scope = new EntryScope(User.GetOrganizationId()!.Value, req.VaultId, req.EntryId);
        var senderId = User.GetUserId()!.Value;
        var source = await authority.LoadSenderSourceAsync(scope, senderId, User.GetAuthorizationVersion()!.Value, ct);
        var now = clock.GetCurrentInstant();
        var lifetime = Duration.FromSeconds(options.Value.ChallengeTtlSeconds);
        var challenge = await context.EntryShareCreationChallenges.SingleOrDefaultAsync(
            x => x.OrganizationId == scope.OrganizationId && x.VaultId == scope.VaultId
                 && x.EntryId == scope.EntryId && x.RequestedBy == senderId, ct);
        if (challenge is null)
        {
            challenge = EntryShareCreationChallenge.Create(scope, senderId, guidProvider.Generate(), now + lifetime);
            context.Add(challenge);
        }
        else if (now >= challenge.ExpiresAt)
        {
            challenge.Renew(guidProvider.Generate(), now, lifetime);
        }

        await context.CommitAsync(ct);
        await Send.OkAsync(new IssueEntryShareCreationChallengeResponse(challenge.ShareId,
            source.Entry.CurrentRevision.Value.ToString(CultureInfo.InvariantCulture), challenge.ExpiresAt), ct);
    }
}
