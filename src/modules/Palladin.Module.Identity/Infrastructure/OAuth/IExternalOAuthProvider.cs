using Palladin.Module.Identity.Contracts.ValueObjects;
using Palladin.Module.Identity.Domain.Enums;

namespace Palladin.Module.Identity.Infrastructure.OAuth;

internal interface IExternalOAuthProvider
{
    AuthProvider Provider { get; }
    Task<ExternalUserInfo> ValidateTokenAsync(string token, CancellationToken ct);
}

internal sealed record ExternalUserInfo(string SubjectId, string Email, bool EmailVerified, string? Name, string? Picture);
