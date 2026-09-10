using System.Text.Json.Serialization;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Api;
using Palladin.Core.Json;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Infrastructure.SharedUnlock;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record ConsumeSharedUnlockOperationRequest
{
    public Guid OperationId { get; init; }
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))] public byte[] Signature { get; init; } = [];
}

[UsedImplicitly]
internal sealed class ConsumeSharedUnlockOperationValidator : Validator<ConsumeSharedUnlockOperationRequest>
{
    public ConsumeSharedUnlockOperationValidator()
    {
        RuleFor(request => request.OperationId).NotEmpty();
        RuleFor(request => request.Signature).Must(value => value is { Length: 64 });
    }
}

[PublicAPI]
internal sealed class ConsumeSharedUnlockOperationEndpoint(IdentityDomainWriteContext context, IClock clock)
    : Endpoint<ConsumeSharedUnlockOperationRequest, SharedUnlockOperationResponse>
{
    public override void Configure()
    {
        Post("api/auth/shared-unlock/operations/{OperationId}/consume");
        AllowAnonymous();
        Summary(summary =>
        {
            summary.Summary = "Consume a source-authorized operation with the receiver's one-time proof";
            summary.Description = "No prior receiver session is required. Ed25519 proof of the source-bound receiver key, "
                + "a live source authority and an atomic one-time transition authenticate this exchange. No session tokens are issued here.";
        });
        Tags("Identity/Auth");
    }

    public override async Task HandleAsync(ConsumeSharedUnlockOperationRequest req, CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var operation = await context.SharedUnlockOperations.SingleOrDefaultAsync(operation => operation.Id == req.OperationId, ct);
        var now = clock.GetCurrentInstant();
        if (operation is null || !SharedUnlockIdentityProof.Verify(SharedUnlockTranscript.Proof(operation),
            SharedUnlockProofPurpose.Consume, req.Signature, now))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }
        var authority = await SharedUnlockOperationAuthority.LoadAsync(context, operation, now, ct);
        if (authority is null || !operation.TryConsume(clock.GetCurrentInstant()))
        {
            await SendUnavailableAsync(ct);
            return;
        }
        authority.Fence(context);
        try
        {
            await context.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await SendUnavailableAsync(ct);
            return;
        }
        await Send.OkAsync(SharedUnlockOperationResponse.From(operation, authority.KeyContext), ct);
    }

    private async Task SendUnavailableAsync(CancellationToken ct)
    {
        AddError(ErrorResponses.General("shared-unlock-operation-unavailable"));
        await Send.ErrorsAsync(StatusCodes.Status409Conflict, ct);
    }
}
