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
using Palladin.Module.Vault.Infrastructure.Authorization;
using Palladin.Module.Vault.Infrastructure.Creation;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record IssueEntryCreationChallengeRequest : IRequiresVaultMembership
{
    public Guid VaultId { get; init; }
    public int Count { get; init; } = 1;
}

[PublicAPI]
public sealed record EntryCreationChallengeContract(Guid EntryId, Instant ExpiresAt);

[PublicAPI]
public sealed record IssueEntryCreationChallengeResponse(IReadOnlyList<EntryCreationChallengeContract> Items);

[UsedImplicitly]
internal sealed class IssueEntryCreationChallengeValidator : Validator<IssueEntryCreationChallengeRequest>
{
    public IssueEntryCreationChallengeValidator()
    {
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.Count).InclusiveBetween(1, 500);
    }
}

[PublicAPI]
internal sealed class IssueEntryCreationChallengeEndpoint(
    VaultDomainWriteContext domainWriteContext,
    IGuidProvider guidProvider,
    IClock clock,
    IOptions<VaultCreationOptions> options)
    : Endpoint<IssueEntryCreationChallengeRequest, IssueEntryCreationChallengeResponse>
{
    public override void Configure()
    {
        Post("api/vaults/{vaultId:guid}/entries/creation-challenges");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.VaultManage);
        this.RequireEmailVerified();
        this.RequireVaultMembership();
        Summary(summary =>
        {
            summary.Summary = "Issue Entry creation challenges";
            summary.Description = "Issues up to 500 short-lived server-owned Entry identifiers so create and import clients can bind every encrypted projection and wrapped Entry key to its final AAD scope.";
        });
        Tags("Vault/Entries");
    }

    public override async Task HandleAsync(IssueEntryCreationChallengeRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId()!.Value;
        var vault = await domainWriteContext.Vaults.FirstOrDefaultAsync(
            x => x.OrganizationId == organizationId && x.Id == req.VaultId, ct);
        if (vault is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var now = clock.GetCurrentInstant();
        var userId = User.GetUserId()!.Value;
        var existing = await domainWriteContext.EntryCreationChallenges
            .Where(x => x.OrganizationId == vault.OrganizationId
                        && x.VaultId == vault.Id
                        && x.RequestedBy == userId)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct);
        var active = existing
            .Where(x => x.IsActiveAt(now))
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.EntryId)
            .Take(req.Count)
            .ToList();
        var retired = existing.Where(x => !x.IsActiveAt(now)).Cast<object>().ToList();
        if (retired.Count > 0)
        {
            domainWriteContext.RemoveRange(retired);
        }

        var missing = req.Count - active.Count;
        if (missing > 0)
        {
            for (var index = 0; index < missing; index++)
            {
                var challenge = EntryCreationChallenge.Create(
                    new EntryScope(vault.OrganizationId, vault.Id, guidProvider.Generate()),
                    userId,
                    now,
                    Duration.FromSeconds(options.Value.ChallengeTtlSeconds));
                domainWriteContext.Add(challenge);
                active.Add(challenge);
            }
        }

        if (missing > 0 || retired.Count > 0)
        {
            await domainWriteContext.CommitAsync(ct);
        }

        await Send.OkAsync(new IssueEntryCreationChallengeResponse(active
            .Select(x => new EntryCreationChallengeContract(x.EntryId, x.ExpiresAt))
            .ToList()), ct);
    }
}
