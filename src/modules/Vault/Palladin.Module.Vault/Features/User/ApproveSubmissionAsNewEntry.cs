using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Palladin.Module.Vault.Infrastructure.Persistence;
using NodaTime;
using Palladin.Core.Security;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record ApproveSubmissionAsNewEntryRequest
{
    public Guid SubmissionId { get; init; }
    public Guid VaultId { get; init; }
    public CreateEntryRequest Entry { get; init; } = null!;
    public CreateGranularGrantRequest? Grant { get; init; }
}

[UsedImplicitly]
internal sealed class ApproveSubmissionAsNewEntryValidator : Validator<ApproveSubmissionAsNewEntryRequest>
{
    public ApproveSubmissionAsNewEntryValidator()
    {
        RuleFor(x => x.SubmissionId).NotEmpty();
        RuleFor(x => x.VaultId).NotEmpty();
        RuleFor(x => x.Entry).NotNull().SetValidator(new CreateEntryValidator());
        RuleFor(x => x.Entry.VaultId).Equal(x => x.VaultId).When(x => x.Entry is not null);
        RuleFor(x => x.Grant!).SetValidator(new CreateGranularGrantValidator()).When(x => x.Grant is not null);
    }
}

[PublicAPI]
internal sealed class ApproveSubmissionAsNewEntryEndpoint(
    VaultDomainWriteContext domainWriteContext,
    IClock clock) : Endpoint<ApproveSubmissionAsNewEntryRequest, ApproveSubmissionResponse>
{
    public override void Configure()
    {
        Post("api/submissions/{submissionId:guid}/approve-new");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.GrantManage);
        this.RequireEmailVerified();
        Summary(summary =>
        {
            summary.Summary = "Approve an Agent submission as a new Entry";
            summary.Description = "Atomically creates the client-encrypted Entry from an existing creation challenge, the optional GRANULAR grant for the submitting Agent and the approved submission state, deleting the submission ciphertext and wrapped keys.";
        });
        Tags("Vault/Submissions");
    }

    public override async Task HandleAsync(ApproveSubmissionAsNewEntryRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId()!.Value;
        var userId = User.GetUserId()!.Value;
        var now = clock.GetCurrentInstant();
        try
        {
            var decision = await SubmissionDecision.LoadAsync(domainWriteContext, organizationId, userId, req.SubmissionId, ct);
            var submission = decision.Submission;
            if (submission.IsApprovedAs(userId, req.VaultId, req.Entry.EntryId))
            {
                var approved = await EntryCreation.FindAsync(domainWriteContext, organizationId, req.VaultId, req.Entry.EntryId, ct);
                await Send.OkAsync(SubmissionDecision.ToResponse(submission, approved!), ct);
                return;
            }

            SubmissionDecision.EnsureApprovable(decision);
            await SubmissionDecision.EnsureVaultManagerAsync(
                domainWriteContext, User.GetPermissions(), organizationId, req.VaultId, userId, ct);
            SubmissionDecision.EnsureGrantTargets(req.Grant, submission, req.VaultId, req.Entry.EntryId);
            if (await EntryCreation.FindAsync(domainWriteContext, organizationId, req.VaultId, req.Entry.EntryId, ct) is not null)
            {
                throw new SubmissionRejection(StatusCodes.Status409Conflict, "entry-exists");
            }

            var entry = await EntryCreation.StageAsync(
                domainWriteContext, req.Entry, EntryCreationMaterial.From(req.Entry), organizationId, userId, now, ct)
                ?? throw new SubmissionRejection(StatusCodes.Status404NotFound, "vault-not-found");
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
            await new SubmissionRejection(StatusCodes.Status409Conflict, "grant-id-used").SendAsync(HttpContext, ct);
        }
        catch (Exception ex) when (SubmissionRejection.Handles(ex))
        {
            await SubmissionRejection.From(ex).SendAsync(HttpContext, ct);
        }
    }
}
