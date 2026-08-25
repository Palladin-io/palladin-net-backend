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
using Npgsql;

namespace Palladin.Module.Identity.Features;

[PublicAPI]
public sealed record JoinWaitlistRequest
{
    public string Email { get; init; } = string.Empty;
    public string? Language { get; init; }
    public string AudienceType { get; init; } = string.Empty;
    public string AgentFramework { get; init; } = string.Empty;
    public string? AgentFrameworkOther { get; init; }
    public string CredentialedWorkflow { get; init; } = string.Empty;
    public string? CurrentWorkaround { get; init; }
    public bool? ReadyWithin30Days { get; init; }
    public string? CampaignSource { get; init; }
    public string PromotionTermsVersion { get; init; } = string.Empty;
}

[PublicAPI]
public sealed record JoinWaitlistResponse(string Status);

[UsedImplicitly]
internal sealed class JoinWaitlistValidator : Validator<JoinWaitlistRequest>
{
    public JoinWaitlistValidator(IOptions<WaitlistOptions> options)
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(320);
        RuleFor(x => x.AudienceType)
            .Must(value => !string.IsNullOrWhiteSpace(value)
                && WaitlistQualification.AudienceTypes.Contains(value.Trim().ToLowerInvariant()));
        RuleFor(x => x.AgentFramework)
            .Must(value => !string.IsNullOrWhiteSpace(value)
                && WaitlistQualification.AgentFrameworks.Contains(value.Trim().ToLowerInvariant()));
        RuleFor(x => x.AgentFrameworkOther)
            .NotEmpty()
            .Must(value => !string.IsNullOrWhiteSpace(value))
            .MaximumLength(WaitlistQualification.AgentFrameworkOtherMaxLength)
            .When(x => string.Equals(x.AgentFramework?.Trim(), "other", StringComparison.OrdinalIgnoreCase));
        RuleFor(x => x.AgentFrameworkOther)
            .Empty()
            .Unless(x => string.Equals(x.AgentFramework?.Trim(), "other", StringComparison.OrdinalIgnoreCase));
        RuleFor(x => x.CredentialedWorkflow)
            .NotEmpty()
            .Must(value => !string.IsNullOrWhiteSpace(value))
            .MaximumLength(WaitlistQualification.CredentialedWorkflowMaxLength);
        RuleFor(x => x.CurrentWorkaround)
            .MaximumLength(WaitlistQualification.CurrentWorkaroundMaxLength);
        RuleFor(x => x.ReadyWithin30Days).NotNull();
        RuleFor(x => x.CampaignSource)
            .MaximumLength(WaitlistQualification.CampaignSourceMaxLength)
            .Must(value => value is null || (!string.IsNullOrWhiteSpace(value)
                && WaitlistQualification.CampaignSources.Contains(value.Trim().ToLowerInvariant())));
        RuleFor(x => x.PromotionTermsVersion)
            .Equal(options.Value.PromotionTermsVersion);
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
        var now = clock.GetCurrentInstant();
        if (!opts.Enabled
            || !opts.BenefitEnabled
            || opts.PublicLaunchAtUtc is null
            || now >= Instant.FromDateTimeOffset(opts.PublicLaunchAtUtc.Value))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var email = req.Email.Trim().ToLowerInvariant();
        var language = NormalizeLanguage(req.Language);
        var qualification = WaitlistQualification.Create(
            req.AudienceType,
            req.AgentFramework,
            req.AgentFrameworkOther,
            req.CredentialedWorkflow,
            req.CurrentWorkaround,
            req.ReadyWithin30Days!.Value,
            req.CampaignSource);
        var existing = await domainWriteContext.WaitlistEntries.FirstOrDefaultAsync(x => x.Email == email, ct);
        if (existing is null)
        {
            var (token, tokenHash) = GenerateToken();
            domainWriteContext.Add(WaitlistEntry.Join(
                guidProvider.Generate(), email, language, qualification, req.PromotionTermsVersion, token, tokenHash,
                Duration.FromHours(opts.TokenTtlHours), now));
            try
            {
                await domainWriteContext.CommitAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException
                   {
                       SqlState: Core.Persistence.PostgresErrorCodes.UniqueViolation,
                       ConstraintName: "IX_WaitlistEntries_Email",
                   })
            {
                // A concurrent request for the same normalized address won the insert race.
                // Keep the public response enumeration-safe and idempotent; the winning
                // transaction owns the single pending entry and verification message.
                domainWriteContext.Clear();
            }
        }
        else if (!existing.IsVerified)
        {
            var changed = existing.UpdatePendingQualification(
                language, qualification, req.PromotionTermsVersion, now);
            var canReissue = existing.CanReissueToken(Duration.FromMinutes(opts.ResendCooldownMinutes), now);
            if (canReissue)
            {
                var (token, tokenHash) = GenerateToken();
                existing.ReissueToken(token, tokenHash, Duration.FromHours(opts.TokenTtlHours), now);
            }

            if (changed || canReissue)
            {
                await domainWriteContext.CommitAsync(ct);
            }
        }

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
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        return (token, TokenService.HashToken(token));
    }
}
