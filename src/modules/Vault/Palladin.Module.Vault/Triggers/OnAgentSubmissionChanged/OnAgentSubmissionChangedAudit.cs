using System.Security.Cryptography;
using System.Text;
using JetBrains.Annotations;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Palladin.Core.Types;
using Palladin.Module.Audit.Contracts.Commands;
using Palladin.Module.Audit.Contracts.ValueObjects;
using Palladin.Module.Vault.Contracts.Events;
using Palladin.Module.Vault.Infrastructure.MassTransit;
using Palladin.Module.Vault.Infrastructure.Persistence;

namespace Palladin.Module.Vault.Triggers;

[UsedImplicitly]
internal sealed class OnAgentSubmissionChangedAuditDefinition : ConsumerDefinition<OnAgentSubmissionChangedAudit>
{
    public OnAgentSubmissionChangedAuditDefinition() => EndpointName = VaultEndpoints.Self;
}

// Audit distinguishes the submitting Agent (Agent actor) from the deciding Member (User actor) and records
// only structural identifiers allowed by the contract, never labels, hosts, usernames or ciphertext.
[UsedImplicitly]
internal sealed class OnAgentSubmissionChangedAudit(
    VaultDomainReadContext domainReadContext,
    IPublishEndpoint publishEndpoint) : IConsumer<AgentSubmissionChangedEvent>
{
    public async Task Consume(ConsumeContext<AgentSubmissionChangedEvent> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;
        var actorType = msg.Action switch
        {
            AgentSubmissionAction.Submitted or AgentSubmissionAction.Rewrapped or AgentSubmissionAction.Used
                => AuditActorType.Agent,
            AgentSubmissionAction.Purged => AuditActorType.System,
            _ => AuditActorType.User,
        };
        var agentName = await domainReadContext.Agents
            .Where(x => x.Id == msg.AgentId && x.OrganizationId == msg.OrganizationId)
            .Select(x => x.Name)
            .FirstOrDefaultAsync(ct);
        var actorName = msg.DecidedBy is { } decidedBy
            ? await domainReadContext.Users.Where(x => x.Id == decidedBy).Select(x => x.DisplayName).FirstOrDefaultAsync(ct)
            : null;
        var metadata = new Dictionary<string, string>
        {
            ["submissionId"] = msg.SubmissionId.ToString(),
            ["submissionKind"] = msg.SubmissionKind.ToString(),
            ["entryType"] = msg.EntryType.ToString(),
            ["state"] = msg.State.ToString(),
        };
        if (msg.DecisionGrantId is { } grantId)
        {
            metadata["grantId"] = grantId.ToString();
        }

        await publishEndpoint.Publish(new AppendAuditLogCommand(
            OrganizationId: msg.OrganizationId,
            EventType: EventTypeOf(msg.Action),
            ActorType: actorType,
            Result: msg.Action == AgentSubmissionAction.Denied ? AuditResult.Denied : AuditResult.Succeeded,
            OccurredAt: msg.OccurredAt,
            UserId: actorType == AuditActorType.User ? msg.DecidedBy : null,
            AgentId: msg.AgentId,
            VaultId: msg.DecisionVaultId ?? msg.TargetVaultId,
            EntryId: msg.DecisionEntryId ?? msg.TargetEntryId,
            AgentName: agentName,
            ActorName: actorType == AuditActorType.User ? actorName : null,
            IpAddress: null,
            Metadata: metadata,
            IdempotencyKey: OccurrenceId(msg)), ct);
    }

    private static string EventTypeOf(AgentSubmissionAction action) => action switch
    {
        AgentSubmissionAction.Submitted => AuditEventType.AgentSubmissionSubmitted,
        AgentSubmissionAction.Rewrapped => AuditEventType.AgentSubmissionRewrapped,
        AgentSubmissionAction.Used => AuditEventType.AgentSubmissionUsed,
        AgentSubmissionAction.Approved => AuditEventType.AgentSubmissionApproved,
        AgentSubmissionAction.Denied => AuditEventType.AgentSubmissionDenied,
        AgentSubmissionAction.Restored => AuditEventType.AgentSubmissionRestored,
        _ => AuditEventType.AgentSubmissionPurged,
    };

    private static Guid OccurrenceId(AgentSubmissionChangedEvent msg)
    {
        var digest = SHA256.HashData(Encoding.ASCII.GetBytes(
            $"PLDN-AUDIT-AGENT-SUBMISSION:{msg.SubmissionId:D}:{(int)msg.Action}:{msg.OccurredAt.ToUnixTimeTicks()}"));
        digest[6] = (byte)((digest[6] & 0x0F) | 0x80);
        digest[8] = (byte)((digest[8] & 0x3F) | 0x80);
        return new Guid(digest.AsSpan(0, 16), bigEndian: true);
    }
}
