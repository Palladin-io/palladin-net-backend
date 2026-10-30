using System.Text.Json.Serialization;
using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using Palladin.Core.Types;
using Palladin.Module.Agents.Infrastructure.AgentAuth;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Submissions;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record AgentSubmissionTargetItem(
    string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? SuggestedVaultId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? VaultId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? EntryId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ExpectedEntryRevision);

[PublicAPI]
public sealed record AgentSubmissionDecisionItem(
    Guid VaultId,
    Guid EntryId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? GrantId);

[PublicAPI]
public sealed record AgentSubmissionItem(
    Guid SubmissionId,
    ushort SubmissionKind,
    ushort EntryType,
    AgentSubmissionTargetItem Target,
    string State,
    bool AgentUseSuspended,
    string SubmissionRevision,
    uint ApproverKeyVersion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] uint? CurrentApproverKeyVersion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AgentSubmissionDecisionItem? Decision,
    Instant CreatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Instant? DecidedAt);

[PublicAPI]
public sealed record ListAgentSubmissionsResponse(IReadOnlyList<AgentSubmissionItem> Items);

[PublicAPI]
internal sealed class ListAgentSubmissionsEndpoint(
    VaultDomainReadContext domainReadContext,
    IOptions<AgentSubmissionOptions> options) : Endpoint<EmptyRequest, ListAgentSubmissionsResponse>
{
    public override void Configure()
    {
        Get("api/agent/submissions");
        AuthSchemes(AgentAuthenticationOptions.SchemeName);
        Summary(summary =>
        {
            summary.Summary = "List the Agent's own submissions";
            summary.Description = "Returns structural lifecycle state of the authenticated Agent's non-purged submissions, newest first, without ciphertext or wrapped keys.";
        });
        Tags("Vault/Submissions");
    }

    public override async Task HandleAsync(EmptyRequest req, CancellationToken ct)
    {
        var agentId = User.GetAgentId();
        var organizationId = User.GetAgentOrganizationId();
        if (agentId is null || organizationId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var currentKeyVersion = await domainReadContext.OrganizationApproverKeySets
            .Where(x => x.OrganizationId == organizationId)
            .Select(x => (uint?)x.KeyVersion)
            .SingleOrDefaultAsync(ct);
        var submissions = await domainReadContext.AgentEntrySubmissions
            .Where(x => x.OrganizationId == organizationId && x.AgentId == agentId
                        && x.State != AgentSubmissionState.Purged)
            .OrderByDescending(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .Take(options.Value.MaximumListItems)
            .ToListAsync(ct);

        await Send.OkAsync(new ListAgentSubmissionsResponse(submissions.Select(x => new AgentSubmissionItem(
            x.Id,
            (ushort)x.Kind,
            (ushort)x.EntryType,
            SubmissionItemMapper.ToTarget(x),
            AgentSubmissionIntake.StateName(x.State),
            x.AgentUseSuspended,
            x.SubmissionRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            x.ApproverKeyVersion,
            currentKeyVersion,
            x.State == AgentSubmissionState.Approved
                ? new AgentSubmissionDecisionItem(x.DecisionVaultId!.Value, x.DecisionEntryId!.Value, x.DecisionGrantId)
                : null,
            x.CreatedAt,
            x.DecidedAt)).ToList()), ct);
    }
}

internal static class SubmissionItemMapper
{
    internal static AgentSubmissionTargetItem ToTarget(Domain.AgentEntrySubmission submission) => new(
        submission.Kind == AgentSubmissionKind.New ? "new" : "update",
        submission.SuggestedVaultId,
        submission.TargetVaultId,
        submission.TargetEntryId,
        submission.ExpectedEntryRevision?.ToString(System.Globalization.CultureInfo.InvariantCulture));
}
