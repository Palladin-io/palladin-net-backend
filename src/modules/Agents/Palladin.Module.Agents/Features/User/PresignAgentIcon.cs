using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Palladin.Core.Security;
using Palladin.Module.Agents.Infrastructure.Persistence;
using Palladin.Module.Agents.Infrastructure.PublicAssets;
using NodaTime;

namespace Palladin.Module.Agents.Features;

[PublicAPI] public sealed record PresignAgentIconRequest(Guid AgentId, string MediaType, long ByteLength, string Sha256);
[PublicAPI] public sealed record PresignAgentIconResponse(Guid AssetId, Guid UploadSessionId, string UploadUrl, long MaximumBytes);
[PublicAPI] public sealed record CompleteAgentIconUploadRequest(Guid AgentId, Guid UploadSessionId);
[PublicAPI] public sealed record CompleteAgentIconUploadResponse(Guid AssetId, string PublicUrl, int Revision);

[UsedImplicitly]
internal sealed class PresignAgentIconValidator : Validator<PresignAgentIconRequest>
{
    public PresignAgentIconValidator()
    {
        RuleFor(x => x.AgentId).NotEmpty();
        RuleFor(x => x.MediaType).Must(x => x is "image/png" or "image/jpeg" or "image/webp");
        RuleFor(x => x.ByteLength).InclusiveBetween(1, 1024 * 1024);
        RuleFor(x => x.Sha256).Matches("^[a-f0-9]{64}$");
    }
}

[PublicAPI]
internal sealed class PresignAgentIconEndpoint(AgentsDomainReadContext db, IPublicAssetCatalogClient catalog) : Endpoint<PresignAgentIconRequest, PresignAgentIconResponse>
{
    public override void Configure() { Post("api/agents/{agentId:guid}/icon/presign"); AuthSchemes(JwtBearerDefaults.AuthenticationScheme); this.RequirePermission(Permission.AgentManage); this.RequireEmailVerified(); Tags("Agents/Agents"); }
    public override async Task HandleAsync(PresignAgentIconRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId(); if (organizationId is null) { await Send.UnauthorizedAsync(ct); return; }
        if (!await db.Agents.AnyAsync(x => x.Id == req.AgentId && x.OrganizationId == organizationId, ct)) { await Send.NotFoundAsync(ct); return; }
        var upload = await catalog.CreateAgentIconUploadAsync(organizationId.Value, req.AgentId, req.MediaType, req.ByteLength, req.Sha256, ct);
        await Send.OkAsync(new(upload.AssetId, upload.UploadSessionId, upload.UploadUrl, upload.MaximumBytes), ct);
    }
}

[PublicAPI]
internal sealed class CompleteAgentIconUploadEndpoint(AgentsDomainWriteContext db, IPublicAssetCatalogClient catalog, IClock clock) : Endpoint<CompleteAgentIconUploadRequest, CompleteAgentIconUploadResponse>
{
    public override void Configure() { Post("api/agents/{agentId:guid}/icon/complete"); AuthSchemes(JwtBearerDefaults.AuthenticationScheme); this.RequirePermission(Permission.AgentManage); this.RequireEmailVerified(); Tags("Agents/Agents"); }
    public override async Task HandleAsync(CompleteAgentIconUploadRequest req, CancellationToken ct)
    {
        var organizationId = User.GetOrganizationId(); if (organizationId is null) { await Send.UnauthorizedAsync(ct); return; }
        var agent = await db.Agents.SingleOrDefaultAsync(x => x.Id == req.AgentId && x.OrganizationId == organizationId, ct); if (agent is null) { await Send.NotFoundAsync(ct); return; }
        var asset = await catalog.CompleteAgentIconUploadAsync(organizationId.Value, req.AgentId, req.UploadSessionId, ct);
        agent.Update(clock.GetCurrentInstant(), null, null, null, $"public-asset:{asset.AssetId}", null);
        await db.CommitAsync(ct);
        await Send.OkAsync(new(asset.AssetId, asset.PublicUrl, asset.Revision), ct);
    }
}
