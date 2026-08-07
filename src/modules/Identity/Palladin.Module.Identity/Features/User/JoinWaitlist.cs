using System.Security.Cryptography;
using Palladin.Core.Guid;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Module.Identity.Infrastructure.Waitlist;
using FastEndpoints;
using FluentValidation;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record JoinWaitlistRequest
{
    public string Email { get; init; } = string.Empty;
    public string? Language { get; init; }
}

[PublicAPI]
public sealed record JoinWaitlistResponse(string Status);

[UsedImplicitly]
internal sealed class JoinWaitlistValidator : Validator<JoinWaitlistRequest>
{
    public JoinWaitlistValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
    }
}

[PublicAPI]
internal sealed class JoinWaitlistEndpoint(
    IdentityDomainWriteContext domainWriteContext,
    IOptions<WaitlistOptions> options,
    IGuidProvider guidProvider,
    IClock clock) : Endpoint<JoinWaitlistRequest, JoinWaitlistResponse>
{
    private const string AcceptedStatus = "accepted";

    public override void Configure()
    {
        Post("api/waitlist");
        AllowAnonymous();
        Summary(summary =>
        {
            summary.Summary = "Join the waitlist";
            summary.Description =
                "Registers an email on the launch waitlist and sends a double opt-in verification link. "
                + "Always returns 202 so registered addresses cannot be enumerated.";
        });
        Tags("Identity/Waitlist");
    }

    public override async Task HandleAsync(JoinWaitlistRequest req, CancellationToken ct)
    {
        var opts = options.Value;
        if (!opts.Enabled)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var email = req.Email.Trim().ToLowerInvariant();
        var language = NormalizeLanguage(req.Language);
        var now = clock.GetCurrentInstant();

        var existing = await domainWriteContext.WaitlistEntries.FirstOrDefaultAsync(x => x.Email == email, ct);
        if (existing is null)
        {
            var (token, tokenHash) = GenerateToken();
            domainWriteContext.Add(WaitlistEntry.Join(
                guidProvider.Generate(), email, language, token, tokenHash,
                Duration.FromHours(opts.TokenTtlHours), now));
            await domainWriteContext.CommitAsync(ct);
        }
        else if (existing.CanReissueToken(Duration.FromMinutes(opts.ResendCooldownMinutes), now))
        {
            var (token, tokenHash) = GenerateToken();
            existing.ReissueToken(token, tokenHash, Duration.FromHours(opts.TokenTtlHours), now);
            await domainWriteContext.CommitAsync(ct);
        }

        // Verified, cooldown-limited and brand-new signups all answer identically (no enumeration).
        await Send.ResponseAsync(new JoinWaitlistResponse(AcceptedStatus), StatusCodes.Status202Accepted, ct);
    }

    private static string NormalizeLanguage(string? language) =>
        language?.Trim().ToLowerInvariant() switch
        {
            "pl" => "pl",
            _ => "en",
        };

    private static (string Token, string TokenHash) GenerateToken()
    {
        // Hex is URL-safe as-is; only the hash is persisted.
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        return (token, TokenService.HashToken(token));
    }
}
