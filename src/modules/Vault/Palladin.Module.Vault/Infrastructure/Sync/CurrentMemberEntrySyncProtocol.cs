using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Palladin.Module.Identity.Contracts.ValueObjects;
using Palladin.Module.Vault.Domain;
using Palladin.Module.Vault.Shared;
using System.Globalization;

namespace Palladin.Module.Vault.Infrastructure.Sync;

internal static class CurrentMemberEntrySyncProtocol
{
    internal const ushort SyncPolicyVersion = 2;
    internal const ushort AccessContextVersion = 1;
    internal const int DefaultPageSize = VaultSyncProtocol.DefaultPageSize;
    internal const int MaxPageSize = VaultSyncProtocol.MaxPageSize;
    internal const int CursorTtlSeconds = VaultSyncProtocol.CursorTtlSeconds;
    internal const uint InitialOfflinePolicyVersion = 1;

    internal static bool IsSupported(HttpContext context) =>
        context.Request.Headers[VaultSyncProtocol.ProtocolHeader].ToString()
            == VaultProtocol.CurrentVersion.ToString()
        && context.Request.Headers[VaultSyncProtocol.SyncPolicyHeader].ToString()
            == SyncPolicyVersion.ToString();

    internal static void ApplyResponseHeaders(HttpContext context)
    {
        context.Response.Headers[VaultSyncProtocol.ProtocolHeader] = VaultProtocol.CurrentVersion.ToString();
        context.Response.Headers[VaultSyncProtocol.SyncPolicyHeader] = SyncPolicyVersion.ToString();
        context.Response.Headers.ContentEncoding = "identity";
    }

    internal static string ToWireValue(OrganizationOfflineAccessPolicy policy) => policy switch
    {
        OrganizationOfflineAccessPolicy.Disabled => "disabled",
        OrganizationOfflineAccessPolicy.OneHour => "1h",
        OrganizationOfflineAccessPolicy.FourHours => "4h",
        OrganizationOfflineAccessPolicy.TwentyFourHours => "24h",
        _ => throw new ArgumentOutOfRangeException(nameof(policy)),
    };

    internal static NodaTime.Duration ToLeaseDuration(OrganizationOfflineAccessPolicy policy) => policy switch
    {
        OrganizationOfflineAccessPolicy.Disabled => NodaTime.Duration.Zero,
        OrganizationOfflineAccessPolicy.OneHour => NodaTime.Duration.FromHours(1),
        OrganizationOfflineAccessPolicy.FourHours => NodaTime.Duration.FromHours(4),
        OrganizationOfflineAccessPolicy.TwentyFourHours => NodaTime.Duration.FromHours(24),
        _ => throw new ArgumentOutOfRangeException(nameof(policy)),
    };

    internal static bool TryParseCanonicalUInt64(string? value, out ulong parsed) =>
        ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed)
        && parsed.ToString(CultureInfo.InvariantCulture) == value;

    internal static Task SendUnsupportedAsync(IEndpoint endpoint, CancellationToken cancellationToken) =>
        endpoint.HttpContext.Response.SendAsync(
            new VaultSyncErrorResponse("unsupported-protocol"),
            StatusCodes.Status426UpgradeRequired,
            cancellation: cancellationToken);

    internal static Task SendInvalidCursorAsync(IEndpoint endpoint, CancellationToken cancellationToken) =>
        endpoint.HttpContext.Response.SendAsync(
            new VaultSyncErrorResponse("invalid-cursor"),
            StatusCodes.Status400BadRequest,
            cancellation: cancellationToken);

    internal static Task SendSizeLimitExceededAsync(IEndpoint endpoint, CancellationToken cancellationToken) =>
        endpoint.HttpContext.Response.SendAsync(
            new VaultSyncErrorResponse("size-limit-exceeded"),
            StatusCodes.Status413PayloadTooLarge,
            cancellation: cancellationToken);

    internal static Task SendStateChangedAsync(IEndpoint endpoint, CancellationToken cancellationToken) =>
        endpoint.HttpContext.Response.SendAsync(
            new VaultSyncErrorResponse("sync-state-changed"),
            StatusCodes.Status409Conflict,
            cancellation: cancellationToken);
}
