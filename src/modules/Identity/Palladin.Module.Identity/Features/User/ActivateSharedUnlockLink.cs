using Palladin.Module.Identity.Shared;
using System.Text.Json.Serialization;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Palladin.Core.Api;
using Palladin.Core.Json;
using Palladin.Core.Security;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.Persistence;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record ActivateSharedUnlockLinkRequest
{
    public Guid LinkId { get; init; }
    public Guid AuthorizationId { get; init; }
    public string RefreshToken { get; init; } = string.Empty;
    [JsonConverter(typeof(Base64UrlByteArrayJsonConverter))]
    public byte[] SourceGeneration { get; init; } = [];
    public uint ExpectedRevision { get; init; }
    public uint ExpectedPreferenceRevision { get; init; }
}

[UsedImplicitly]
internal sealed class ActivateSharedUnlockLinkValidator : Validator<ActivateSharedUnlockLinkRequest>
{
    public ActivateSharedUnlockLinkValidator()
    {
        RuleFor(request => request.LinkId).NotEmpty();
        RuleFor(request => request.AuthorizationId).NotEmpty();
        RuleFor(request => request.RefreshToken).NotEmpty().MaximumLength(1024);
        RuleFor(request => request.SourceGeneration).Must(value => value is { Length: 32 });
        RuleFor(request => request.ExpectedRevision).GreaterThan(0u);
        RuleFor(request => request.ExpectedPreferenceRevision).GreaterThan(0u);
    }
}

[PublicAPI]
internal sealed class ActivateSharedUnlockLinkEndpoint(ActivateSharedUnlockLinkOperation operation) : IdentityOperationEndpoint<ActivateSharedUnlockLinkRequest, SharedUnlockLinkResponse>
{
    public override void Configure()
    {
        Post("api/account/shared-unlock/links/{LinkId}/activate");
        AuthSchemes(JwtBearerDefaults.AuthenticationScheme);
        Tags("Identity/Account");
        Summary(summary => summary.Summary = "Bind current manual-unlock authority to a verified local browser link");
    }

    public override async Task HandleAsync(ActivateSharedUnlockLinkRequest req, CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var result = await operation.ExecuteAsync(req, HttpContext, ct);
        await SendResultAsync(result, ct);
    }
}
