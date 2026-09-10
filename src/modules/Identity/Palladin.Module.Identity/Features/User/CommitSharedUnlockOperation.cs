using System.Text.Json.Serialization;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using Palladin.Core.Api;
using Palladin.Core.Json;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Infrastructure.SharedUnlock;
using Palladin.Core.Guid;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Shared;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record CommitSharedUnlockOperationRequest
{
    public Guid OperationId { get; init; }
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[] Signature { get; init; } = [];
}

[PublicAPI]
public sealed record CommitSharedUnlockOperationResponse(AuthSessionResponse Session,
    Guid AuthorizationId, uint AuthorizationSequence, SharedUnlockTransferContext Context);

[UsedImplicitly]
internal sealed class CommitSharedUnlockOperationValidator : Validator<CommitSharedUnlockOperationRequest>
{
    public CommitSharedUnlockOperationValidator()
    {
        RuleFor(request => request.OperationId).NotEmpty();
        RuleFor(request => request.Signature).Must(value => value is { Length: 64 });
    }
}

[PublicAPI]
internal sealed class CommitSharedUnlockOperationEndpoint(IdentityDomainWriteContext context,
    IAuthSessionIssuer sessionIssuer, IGuidProvider guidProvider, IClock clock, IOptionsMonitor<SharedUnlockOptions> options)
    : Endpoint<CommitSharedUnlockOperationRequest, CommitSharedUnlockOperationResponse>
{
    public override void Configure()
    {
        Post("api/auth/shared-unlock/operations/{OperationId}/commit");
        AllowAnonymous();
        Summary(summary =>
        {
            summary.Summary = "Commit the receiver's proof and issue its own independent session";
            summary.Description = "The receiver authenticates with the source-bound Ed25519 key after consume. "
                + "Current source, account, link, membership, key and time authority is atomically checked with session issuance. "
                + "No source token or MK is returned.";
        });
        Tags("Identity/Auth");
    }

    public override async Task HandleAsync(CommitSharedUnlockOperationRequest req, CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        if (!options.CurrentValue.Enabled)
        {
            await Send.StatusCodeAsync(StatusCodes.Status503ServiceUnavailable, ct);
            return;
        }

        var operation = await context.SharedUnlockOperations.SingleOrDefaultAsync(operation => operation.Id == req.OperationId, ct);
        var now = clock.GetCurrentInstant();
        if (operation is null || !SharedUnlockIdentityProof.Verify(SharedUnlockTranscript.Proof(operation),
            SharedUnlockProofPurpose.Commit, req.Signature, now))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }
        var authority = await SharedUnlockOperationAuthority.LoadAsync(context, operation, now, ct);
        if (authority is null || operation.State != SharedUnlockOperationState.Consumed)
        {
            await SendUnavailableAsync(ct);
            return;
        }
        var issued = sessionIssuer.IssueWithSession(authority.User, authority.TargetOrganization,
            authority.TargetMember.EffectivePermissions(), authority.TargetMember.AuthorizationVersion,
            clock.GetCurrentInstant(), authority.Source.SecondFactorRevision, authority.Source.SecondFactorVerifiedAt);
        if (!operation.TryCommit(issued.Session.SessionId ?? issued.Session.Id, clock.GetCurrentInstant()))
        {
            await SendUnavailableAsync(ct);
            return;
        }
        var inherited = SharedUnlockAuthorization.Inherit(guidProvider.Generate(), issued.Session, authority.Source, operation);
        context.Add(inherited);
        authority.Fence(context);
        if (!options.CurrentValue.Enabled)
        {
            await Send.StatusCodeAsync(StatusCodes.Status503ServiceUnavailable, ct);
            return;
        }
        try
        {
            await context.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await SendUnavailableAsync(ct);
            return;
        }
        await Send.OkAsync(new CommitSharedUnlockOperationResponse(new AuthSessionResponse(
            issued.AccessToken, issued.RefreshToken, authority.User.Id, authority.User.IsOnboarded,
            authority.User.EmailVerified, authority.User.ActiveWaitlistDeveloperBenefitStartedAt(now),
            authority.User.ActiveWaitlistDeveloperBenefitEndsAt(now)), inherited.Id, inherited.Sequence,
            SharedUnlockOperationResponse.From(operation, authority.KeyContext).Context), ct);
    }

    private async Task SendUnavailableAsync(CancellationToken ct)
    {
        AddError(ErrorResponses.General("shared-unlock-operation-unavailable"));
        await Send.ErrorsAsync(StatusCodes.Status409Conflict, ct);
    }
}
