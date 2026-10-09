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
internal sealed class CommitSharedUnlockOperationEndpoint(CommitSharedUnlockOperationHandler operation) : IdentityOperationEndpoint<CommitSharedUnlockOperationRequest, CommitSharedUnlockOperationResponse>
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
        var result = await operation.ExecuteAsync(req, HttpContext, ct);
        await SendResultAsync(result, ct);
    }
}
