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

internal sealed class CommitSharedUnlockOperationHandler(IdentityDomainWriteContext context,
    IAuthSessionIssuer sessionIssuer, IGuidProvider guidProvider, IClock clock, IOptionsMonitor<SharedUnlockOptions> options)
{
    public async Task<IdentityOperationResult<CommitSharedUnlockOperationResponse>> ExecuteAsync(CommitSharedUnlockOperationRequest req, HttpContext httpContext, CancellationToken ct)
    {
        if (!options.CurrentValue.Enabled)
        {
            return IdentityOperationResult<CommitSharedUnlockOperationResponse>.Status(StatusCodes.Status503ServiceUnavailable);
        }

        var operation = await context.SharedUnlockOperations.SingleOrDefaultAsync(operation => operation.Id == req.OperationId, ct);
        var now = clock.GetCurrentInstant();
        if (operation is null || !SharedUnlockIdentityProof.Verify(SharedUnlockTranscript.Proof(operation),
            SharedUnlockProofPurpose.Commit, req.Signature, now))
        {
            return IdentityOperationResult<CommitSharedUnlockOperationResponse>.Status(401);
        }
        var authority = await SharedUnlockOperationAuthority.LoadAsync(context, operation, now, ct);
        if (authority is null || operation.State != SharedUnlockOperationState.Consumed)
        {
            return SendUnavailableAsync(ct);
        }
        var issued = sessionIssuer.IssueWithSession(authority.User, authority.TargetOrganization,
            authority.TargetMember.EffectivePermissions(), authority.TargetMember.AuthorizationVersion,
            clock.GetCurrentInstant(), authority.Source.SecondFactorRevision, authority.Source.SecondFactorVerifiedAt);
        if (!operation.TryCommit(issued.Session.SessionId ?? issued.Session.Id, clock.GetCurrentInstant()))
        {
            return SendUnavailableAsync(ct);
        }
        var inherited = SharedUnlockAuthorization.Inherit(guidProvider.Generate(), issued.Session, authority.Source, operation);
        context.Add(inherited);
        authority.Fence(context);
        if (!options.CurrentValue.Enabled)
        {
            return IdentityOperationResult<CommitSharedUnlockOperationResponse>.Status(StatusCodes.Status503ServiceUnavailable);
        }
        try
        {
            await context.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return SendUnavailableAsync(ct);
        }
        return IdentityOperationResult<CommitSharedUnlockOperationResponse>.Ok(new CommitSharedUnlockOperationResponse(new AuthSessionResponse(
            issued.AccessToken, issued.RefreshToken, authority.User.Id, authority.User.IsOnboarded,
            authority.User.EmailVerified, authority.User.ActiveWaitlistDeveloperBenefitStartedAt(now),
            authority.User.ActiveWaitlistDeveloperBenefitEndsAt(now)), inherited.Id, inherited.Sequence,
            SharedUnlockOperationResponse.From(operation, authority.KeyContext).Context));
    }

    private static IdentityOperationResult<CommitSharedUnlockOperationResponse> SendUnavailableAsync(CancellationToken ct)
    {
        return IdentityOperationResult<CommitSharedUnlockOperationResponse>.Failure(StatusCodes.Status409Conflict, ErrorResponses.General("shared-unlock-operation-unavailable"));
    }
}
