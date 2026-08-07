using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;
using Palladin.Core.Guid;
using Palladin.Core.Security;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Creation;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record IssueVaultCreationChallengeResponse(Guid VaultId, Instant ExpiresAt);

[PublicAPI]
internal sealed class IssueVaultCreationChallengeEndpoint(
    VaultDomainWriteContext domainWriteContext,
    IGuidProvider guidProvider,
    IClock clock,
    IOptions<VaultCreationOptions> options)
    : EndpointWithoutRequest<IssueVaultCreationChallengeResponse>
{
    public override void Configure()
    {
        Post("api/vaults/creation-challenges");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.VaultCreate);
        this.RequireEmailVerified();
        Summary(summary =>
        {
            summary.Summary = "Issue a Vault creation challenge";
            summary.Description = "Issues a short-lived server-owned Vault identifier. The client binds encrypted Vault metadata and the creator key envelope to this identifier before creating the Vault.";
        });
        Tags("Vault/Vaults");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        var organizationId = User.GetOrganizationId()!.Value;
        var userId = User.GetUserId()!.Value;
        var existing = await domainWriteContext.VaultCreationChallenges
            .Where(x => x.OrganizationId == organizationId && x.RequestedBy == userId)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct);
        var active = existing.FirstOrDefault(x => x.IsActiveAt(now));
        var retired = existing.Where(x => !ReferenceEquals(x, active)).Cast<object>().ToList();
        if (retired.Count > 0)
        {
            domainWriteContext.RemoveRange(retired);
        }

        if (active is not null)
        {
            if (retired.Count > 0)
            {
                await domainWriteContext.CommitAsync(ct);
            }

            await Send.OkAsync(new IssueVaultCreationChallengeResponse(active.VaultId, active.ExpiresAt), ct);
            return;
        }

        var challenge = VaultCreationChallenge.Create(
            organizationId,
            guidProvider.Generate(),
            userId,
            now,
            Duration.FromSeconds(options.Value.ChallengeTtlSeconds));

        domainWriteContext.Add(challenge);
        try
        {
            await domainWriteContext.CommitAsync(ct);
        }
        catch (Exception ex) when (IsConcurrentReplacement(ex))
        {
            // A simultaneous request either inserted the unique row first or replaced the same
            // retired row first. Return that winner instead of leaking a uniqueness/concurrency
            // conflict or creating an unbounded second row.
            domainWriteContext.Clear();
            var winner = await domainWriteContext.VaultCreationChallenges
                .SingleAsync(x => x.OrganizationId == organizationId && x.RequestedBy == userId, ct);
            if (!winner.IsActiveAt(clock.GetCurrentInstant()))
            {
                ThrowError("The concurrent Vault creation challenge is no longer active. Retry the request.");
            }

            await Send.OkAsync(new IssueVaultCreationChallengeResponse(winner.VaultId, winner.ExpiresAt), ct);
            return;
        }

        await Send.OkAsync(new IssueVaultCreationChallengeResponse(challenge.VaultId, challenge.ExpiresAt), ct);
    }

    private static bool IsConcurrentReplacement(Exception exception) =>
        exception is DbUpdateConcurrencyException
        || exception is DbUpdateException
        {
            InnerException: PostgresException
            {
                SqlState: Palladin.Core.Persistence.PostgresErrorCodes.UniqueViolation,
            },
        };
}
