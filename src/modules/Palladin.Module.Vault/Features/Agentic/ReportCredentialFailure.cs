using System.Text.Json;
using System.Text.Json.Serialization;
using Palladin.Core.Guid;
using Palladin.Core.Types;
using Palladin.Module.Agents.Infrastructure.AgentAuth;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Persistence;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record ReportCredentialFailureRequest
{
    public Guid VaultId { get; init; }
    public Guid EntryId { get; init; }
    public CredentialFailureCode Code { get; init; } = CredentialFailureCode.Manual;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnexpectedFields { get; init; }
}

[UsedImplicitly]
internal sealed class ReportCredentialFailureValidator : Validator<ReportCredentialFailureRequest>
{
    public ReportCredentialFailureValidator()
    {
        RuleFor(x => x.Code).IsInEnum();
        RuleFor(x => x.UnexpectedFields).Empty();
    }
}

[PublicAPI]
public sealed record ReportCredentialFailureResponse(Guid ReportId);

[PublicAPI]
internal sealed class ReportCredentialFailureEndpoint(
    VaultDomainReadContext domainReadContext,
    VaultDomainWriteContext domainWriteContext,
    IGuidProvider guidProvider,
    IClock clock) : Endpoint<ReportCredentialFailureRequest, ReportCredentialFailureResponse>
{
    public override void Configure()
    {
        Post("api/agent/vaults/{vaultId:guid}/entries/{entryId:guid}/credential-failure");
        AuthSchemes(AgentAuthenticationOptions.SchemeName);
        Summary(summary =>
        {
            summary.Summary = "Report a stored credential as not working";
            summary.Description = "An agent reports that a stored credential failed (login refused, auth failed, or a manual report). The backend records only a closed structural code and notifies the entry's vault members (credential_stale) so a human can rotate or replace the credential — reporting never mints a new credential and never accepts free-form diagnostic text.";
        });
        Tags("Vault/Agents");
    }

    public override async Task HandleAsync(ReportCredentialFailureRequest req, CancellationToken ct)
    {
        var agentId = User.GetAgentId();
        var organizationId = User.GetAgentOrganizationId();
        var accessEpoch = User.GetAgentAccessEpoch();
        if (agentId is null || organizationId is null || accessEpoch is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var agent = await domainReadContext.Agents
            .Where(a => a.Id == agentId.Value
                        && a.OrganizationId == organizationId.Value
                        && a.AccessEpoch == accessEpoch.Value)
            .Select(a => new { a.OrganizationId, a.Status, a.Name })
            .FirstOrDefaultAsync(ct);
        if (agent is null || agent.Status != AgentStatus.Active)
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var entryExists = await domainReadContext.Entries
            .Where(e => e.OrganizationId == agent.OrganizationId
                        && e.VaultId == req.VaultId
                        && e.Id == req.EntryId)
            .AnyAsync(ct);
        var vaultOrganizationId = await domainReadContext.Vaults
            .Where(v => v.OrganizationId == agent.OrganizationId && v.Id == req.VaultId)
            .Select(v => (Guid?)v.OrganizationId)
            .FirstOrDefaultAsync(ct);
        if (!entryExists || vaultOrganizationId != agent.OrganizationId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var report = CredentialFailureReport.Create(
            guidProvider.Generate(),
            agent.OrganizationId,
            req.VaultId,
            req.EntryId,
            agentId.Value,
            req.Code,
            clock.GetCurrentInstant());

        domainWriteContext.Add(report);
        await domainWriteContext.CommitAsync(ct);

        await Send.OkAsync(new ReportCredentialFailureResponse(report.Id), ct);
    }
}
