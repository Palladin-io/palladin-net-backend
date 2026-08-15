using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Shared;

namespace Palladin.Module.Vault.Infrastructure.Sync;

internal static class VaultSyncProtocol
{
    internal const ushort SyncPolicyVersion = 1;
    internal const string ProtocolHeader = "X-Palladin-Vault-Protocol";
    internal const string SyncPolicyHeader = "X-Palladin-Sync-Policy";
    internal const int DefaultPageSize = 100;
    internal const int MaxPageSize = 200;
    internal const int OperationalResponseBytes = 2 * 1024 * 1024;
    internal const int CursorTtlSeconds = 900;
    internal const string HeadKind = "head";
    internal const string TombstoneKind = "tombstone";

    internal static bool IsSupported(HttpContext context) =>
        context.Request.Headers[ProtocolHeader].ToString() == VaultProtocol.CurrentVersion.ToString()
        && context.Request.Headers[SyncPolicyHeader].ToString() == SyncPolicyVersion.ToString();

    internal static void ApplyResponseHeaders(HttpContext context)
    {
        context.Response.Headers[ProtocolHeader] = VaultProtocol.CurrentVersion.ToString();
        context.Response.Headers[SyncPolicyHeader] = SyncPolicyVersion.ToString();
        context.Response.Headers.ContentEncoding = "identity";
    }

    internal static Task SendUnsupportedAsync(IEndpoint ep, CancellationToken ct) =>
        ep.HttpContext.Response.SendAsync(
            new VaultSyncErrorResponse("unsupported-protocol"),
            426,
            cancellation: ct);

    internal static Task SendInvalidCursorAsync(IEndpoint ep, CancellationToken ct) =>
        ep.HttpContext.Response.SendAsync(
            new VaultSyncErrorResponse("invalid-cursor"),
            400,
            cancellation: ct);

    internal static Task SendSizeLimitExceededAsync(IEndpoint ep, CancellationToken ct) =>
        ep.HttpContext.Response.SendAsync(
            new VaultSyncErrorResponse("size-limit-exceeded"),
            413,
            cancellation: ct);

    internal static Task SendStateChangedAsync(IEndpoint ep, CancellationToken ct) =>
        ep.HttpContext.Response.SendAsync(
            new VaultSyncErrorResponse("sync-state-changed"),
            409,
            cancellation: ct);
}
