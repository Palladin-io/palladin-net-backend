using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record ApproveSubmissionAsUpdateRequest
{
    public Guid SubmissionId { get; init; }
    public UpdateEntryRequest Entry { get; init; } = null!;
    public CreateGranularGrantRequest? Grant { get; init; }
}

[UsedImplicitly]
internal sealed class ApproveSubmissionAsUpdateValidator : Validator<ApproveSubmissionAsUpdateRequest>
{
    public ApproveSubmissionAsUpdateValidator()
    {
        RuleFor(x => x.SubmissionId).NotEmpty();
        RuleFor(x => x.Entry).NotNull().SetValidator(new UpdateEntryValidator());
        RuleFor(x => x.Grant!).SetValidator(new CreateGranularGrantValidator()).When(x => x.Grant is not null);
    }
}

[PublicAPI]
internal sealed class ApproveSubmissionAsUpdateEndpoint(
    VaultDomainWriteContext domainWriteContext,
    IClock clock) : Endpoint<ApproveSubmissionAsUpdateRequest, ApproveSubmissionResponse>
{
    public override void Configure()
    {
        Post("api/submissions/{submissionId:guid}/approve-update");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.GrantManage);
        this.RequireEmailVerified();
        Summary(summary =>
        {
            summary.Summary = "Approve an Agent update submission as the next Entry revision";
            summary.Description = "Atomically appends the client-encrypted next revision of the target Entry with every required grant refresh, an optional GRANULAR grant and the approved submission state. A target that moved past the submission's expected revision is rejected, never overwritten.";
        });
        Tags("Vault/Submissions");
    }

    public override async Task HandleAsync(ApproveSubmissionAsUpdateRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId()!.Value;
        var userId = User.GetUserId()!.Value;
        var now = clock.GetCurrentInstant();
        try
        {
            var decision = await SubmissionDecision.LoadAsync(domainWriteContext, organizationId, userId, req.SubmissionId, ct);
            var submission = decision.Submission;
            if (submission.IsApprovedAs(userId, req.Entry.VaultId, req.Entry.EntryId))
            {
                var approved = await EntryCreation.FindAsync(domainWriteContext, organizationId, req.Entry.VaultId, req.Entry.EntryId, ct);
                await Send.OkAsync(SubmissionDecision.ToResponse(submission, approved!), ct);
                return;
            }

            SubmissionDecision.EnsureApprovable(decision);
            if (submission.Kind != AgentSubmissionKind.Update)
            {
                throw new SubmissionRejection(StatusCodes.Status409Conflict, "not-an-update");
            }

            if (req.Entry.VaultId != submission.TargetVaultId || req.Entry.EntryId != submission.TargetEntryId)
            {
                throw new SubmissionRejection(StatusCodes.Status400BadRequest, "target-mismatch");
            }

            await SubmissionDecision.EnsureVaultManagerAsync(
                domainWriteContext, User.GetPermissions(), organizationId, req.Entry.VaultId, userId, ct);
            SubmissionDecision.EnsureGrantTargets(req.Grant, submission, req.Entry.VaultId, req.Entry.EntryId);
            var expected = submission.ExpectedEntryRevision!.Value;
            var currentRevision = await domainWriteContext.Entries
                .Where(x => x.OrganizationId == organizationId && x.VaultId == req.Entry.VaultId && x.Id == req.Entry.EntryId)
                .Select(x => (ulong?)x.CurrentRevision.Value)
                .SingleOrDefaultAsync(ct)
                ?? throw new SubmissionRejection(StatusCodes.Status404NotFound, "entry-not-found");
            if (currentRevision != expected
                || req.Entry.BaseRevision != expected.ToString(System.Globalization.CultureInfo.InvariantCulture))
            {
                throw new SubmissionRejection(StatusCodes.Status409Conflict, "stale-target-revision");
            }

            var staging = await EntryUpdate.StageAsync(
                domainWriteContext, req.Entry, organizationId, userId, User.GetPermissions(), now, ct);
            if (staging.Failure is { } failure)
            {
                throw new SubmissionRejection(failure.StatusCode, "entry-update-rejected");
            }

            if (staging.IsExactRetry)
            {
                throw new SubmissionRejection(StatusCodes.Status409Conflict, "stale-target-revision");
            }

            var entry = staging.Entry!;
            var grantId = await SubmissionDecision.StageGrantAsync(
                domainWriteContext, req.Grant, organizationId, userId, now, entry, ct);
            submission.Approve(userId, entry.VaultId, entry.Id, grantId, now);
            await domainWriteContext.CommitAsync(ct);
            await Send.OkAsync(SubmissionDecision.ToResponse(submission, entry), ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await (await SubmissionDecision.ConcurrencyRejectionAsync(domainWriteContext, organizationId, req.SubmissionId, ct))
                .SendAsync(HttpContext, ct);
        }
        catch (DbUpdateException ex) when (GranularGrantCreation.IsGrantIdentifierConflict(ex))
        {
            await (await SubmissionDecision.ConcurrencyRejectionAsync(
                    domainWriteContext, organizationId, req.SubmissionId, ct, pendingCode: "grant-id-used"))
                .SendAsync(HttpContext, ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException
        {
            SqlState: Palladin.Core.Persistence.PostgresErrorCodes.UniqueViolation,
        })
        {
            await (await SubmissionDecision.ConcurrencyRejectionAsync(domainWriteContext, organizationId, req.SubmissionId, ct))
                .SendAsync(HttpContext, ct);
        }
        catch (Exception ex) when (SubmissionRejection.Handles(ex))
        {
            await SubmissionRejection.From(ex).SendAsync(HttpContext, ct);
        }
    }
}
