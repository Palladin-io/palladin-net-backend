using FastEndpoints;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using Palladin.Core.Guid;
using Palladin.Core.Security;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Infrastructure.Crypto;
using Palladin.Module.Vault.Infrastructure.Persistence;
using Palladin.Module.Vault.Infrastructure.Submissions;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Features;

[PublicAPI]
public sealed record IssueApproverKeyChallengeResponse(
    Guid ChallengeId,
    string IssuedAt,
    string ApproverSetRevision,
    string ApproverSetDigest,
    IReadOnlyList<ApproverContract> Approvers,
    string ExpiresAt);

[PublicAPI]
internal sealed class IssueApproverKeyChallengeEndpoint(
    VaultDomainWriteContext domainWriteContext,
    IGuidProvider guidProvider,
    IOptions<AgentSubmissionOptions> options,
    IClock clock) : Endpoint<EmptyRequest, IssueApproverKeyChallengeResponse>
{
    public override void Configure()
    {
        Post("api/approver-keys/challenge");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        this.RequirePermission(Permission.GrantManage);
        this.RequireEmailVerified();
        Summary(summary =>
        {
            summary.Summary = "Issue a single-use approver manifest challenge";
            summary.Description = "Binds the next approver set revision, the authoritative approver set digest and the server-owned issuedAt that the next signed approver manifest must carry.";
        });
        Tags("Vault/Submissions");
    }

    public override async Task HandleAsync(EmptyRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId()!.Value;
        var userId = User.GetUserId()!.Value;
        var state = await domainWriteContext.LoadApproverStateAsync(organizationId, ct);
        if (!state.TryGet(userId, out _))
        {
            await new SubmissionRejection(StatusCodes.Status403Forbidden, "not-an-approver").SendAsync(HttpContext, ct);
            return;
        }

        var issuedAt = AgentSubmissionCrypto.TruncateToSeconds(clock.GetCurrentInstant());
        var lifetime = Duration.FromSeconds(options.Value.ApproverChallengeLifetimeSeconds);
        var approverSetRevision = checked((state.KeySet?.ApproverSetRevision ?? 0) + 1);
        var id = guidProvider.Generate();
        var challenge = await domainWriteContext.ApproverKeyChallenges
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.IssuedTo == userId, ct);
        if (challenge is null)
        {
            challenge = ApproverKeyChallenge.Issue(
                organizationId, userId, id, issuedAt, lifetime, approverSetRevision, state.Digest);
            domainWriteContext.Add(challenge);
        }
        else
        {
            challenge.Reissue(id, issuedAt, lifetime, approverSetRevision, state.Digest);
        }

        await domainWriteContext.CommitAsync(ct);
        await Send.OkAsync(new IssueApproverKeyChallengeResponse(
            challenge.Id,
            AgentSubmissionCrypto.FormatIssuedAt(challenge.IssuedAt),
            approverSetRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            AgentSubmissionCrypto.Encode(state.Digest),
            state.Approvers.Select(x => ApproverKeyContractMapper.ToContract(x, state.IsServed(x.MemberId))).ToList(),
            AgentSubmissionCrypto.FormatIssuedAt(challenge.ExpiresAt)), ct);
    }
}
