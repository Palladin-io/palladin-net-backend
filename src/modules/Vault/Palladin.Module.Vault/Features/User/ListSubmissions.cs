using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using Palladin.Core.Security;
using Palladin.Core.Types;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Submissions;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record ListSubmissionsRequest
{
    [QueryParam]
    public string State { get; init; } = "pending";
}

[PublicAPI]
public sealed record SubmissionListItem(
    Guid SubmissionId,
    Guid AgentId,
    string AgentName,
    ushort SubmissionKind,
    ushort EntryType,
    AgentSubmissionTargetItem Target,
    Guid? SuggestedVaultId,
    string State,
    bool AgentUseSuspended,
    Instant CreatedAt,
    uint ApproverKeyVersion);

[PublicAPI]
public sealed record ListSubmissionsResponse(IReadOnlyList<SubmissionListItem> Items);

[UsedImplicitly]
internal sealed class ListSubmissionsValidator : Validator<ListSubmissionsRequest>
{
    public ListSubmissionsValidator() => RuleFor(x => x.State).Must(x => x is "pending" or "trashed");
}

[PublicAPI]
internal sealed class ListSubmissionsEndpoint(
    VaultDomainReadContext domainReadContext,
    IOptions<AgentSubmissionOptions> options) : Endpoint<ListSubmissionsRequest, ListSubmissionsResponse>
{
    public override void Configure()
    {
        Get("api/submissions");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.GrantManage);
        this.RequireEmailVerified();
        Summary(summary =>
        {
            summary.Summary = "List Agent submissions awaiting a decision or in trash";
            summary.Description = "Returns structural submission rows with the submitting Agent's name, newest first. Ciphertext, labels and hosts are never included.";
        });
        Tags("Vault/Submissions");
    }

    public override async Task HandleAsync(ListSubmissionsRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId()!.Value;
        var state = req.State == "trashed" ? AgentSubmissionState.Trashed : AgentSubmissionState.Pending;
        var rows = await domainReadContext.AgentEntrySubmissions
            .Where(x => x.OrganizationId == organizationId && x.State == state)
            .OrderByDescending(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .Take(options.Value.MaximumListItems)
            .Select(x => new
            {
                Submission = x,
                AgentName = domainReadContext.Agents
                    .Where(agent => agent.Id == x.AgentId && agent.OrganizationId == organizationId)
                    .Select(agent => agent.Name)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        await Send.OkAsync(new ListSubmissionsResponse(rows.Select(row => new SubmissionListItem(
            row.Submission.Id,
            row.Submission.AgentId,
            row.AgentName ?? Domain.GrantNames.UnknownAgent,
            (ushort)row.Submission.Kind,
            (ushort)row.Submission.EntryType,
            SubmissionItemMapper.ToTarget(row.Submission),
            row.Submission.SuggestedVaultId,
            AgentSubmissionIntake.StateName(row.Submission.State),
            row.Submission.AgentUseSuspended,
            row.Submission.CreatedAt,
            row.Submission.ApproverKeyVersion)).ToList()), ct);
    }
}
