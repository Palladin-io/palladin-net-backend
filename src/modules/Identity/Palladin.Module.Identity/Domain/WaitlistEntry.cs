using Palladin.Core.Events;
using Palladin.Module.Identity.Contracts.Events;
using NodaTime;

namespace Palladin.Module.Identity.Domain;

internal sealed class WaitlistEntry : EventEntityBase
{
    internal const string DeveloperPlan = "Developer";
    internal const string ReservedBenefitStatus = "reserved";
    internal const string CurrentPromotionTermsVersion = "waitlist-developer-v1";

    public Guid Id { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public string Language { get; private set; } = "en";
    public string? AudienceType { get; private set; }
    public string? AgentFramework { get; private set; }
    public string? AgentFrameworkOther { get; private set; }
    public string? CredentialedWorkflow { get; private set; }
    public string? CurrentWorkaround { get; private set; }
    public bool? ReadyWithin30Days { get; private set; }
    public string? CampaignSource { get; private set; }
    public string? PromotionTermsVersion { get; private set; }
    public Instant? PromotionTermsAcceptedAt { get; private set; }
    public string TokenHash { get; private set; } = string.Empty;
    public Instant TokenIssuedAt { get; private set; }
    public Instant TokenExpiresAt { get; private set; }
    public Instant? VerifiedAt { get; private set; }
    public Guid? BenefitUserId { get; private set; }
    public string? BenefitPlan { get; private set; }
    public string? BenefitStatus { get; private set; }
    public Instant? BenefitEligibleAt { get; private set; }
    public Instant? BenefitStartsAt { get; private set; }
    public Instant? BenefitEndsAt { get; private set; }
    public Instant CreatedAt { get; private set; }

    private WaitlistEntry() { }

    internal static WaitlistEntry Join(
        Guid id,
        string normalizedEmail,
        string language,
        WaitlistQualification qualification,
        string promotionTermsVersion,
        string token,
        string tokenHash,
        Duration tokenTtl,
        Instant now)
    {
        var entry = new WaitlistEntry
        {
            Id = id,
            Email = normalizedEmail,
            Language = language,
            AudienceType = qualification.AudienceType,
            AgentFramework = qualification.AgentFramework,
            AgentFrameworkOther = qualification.AgentFrameworkOther,
            CredentialedWorkflow = qualification.CredentialedWorkflow,
            CurrentWorkaround = qualification.CurrentWorkaround,
            ReadyWithin30Days = qualification.ReadyWithin30Days,
            CampaignSource = qualification.CampaignSource,
            PromotionTermsVersion = promotionTermsVersion,
            PromotionTermsAcceptedAt = now,
            TokenHash = tokenHash,
            TokenIssuedAt = now,
            TokenExpiresAt = now + tokenTtl,
            CreatedAt = now,
        };
        entry.EmitJoined(token);
        return entry;
    }

    internal bool CanReissueToken(Duration resendCooldown, Instant now) =>
        VerifiedAt is null && now - TokenIssuedAt >= resendCooldown;

    internal bool UpdatePendingQualification(
        string language,
        WaitlistQualification qualification,
        string promotionTermsVersion,
        Instant now)
    {
        if (VerifiedAt is not null)
        {
            return false;
        }

        var changed = Language != language
            || AudienceType != qualification.AudienceType
            || AgentFramework != qualification.AgentFramework
            || AgentFrameworkOther != qualification.AgentFrameworkOther
            || CredentialedWorkflow != qualification.CredentialedWorkflow
            || CurrentWorkaround != qualification.CurrentWorkaround
            || ReadyWithin30Days != qualification.ReadyWithin30Days
            || CampaignSource != qualification.CampaignSource
            || PromotionTermsVersion != promotionTermsVersion;

        Language = language;
        AudienceType = qualification.AudienceType;
        AgentFramework = qualification.AgentFramework;
        AgentFrameworkOther = qualification.AgentFrameworkOther;
        CredentialedWorkflow = qualification.CredentialedWorkflow;
        CurrentWorkaround = qualification.CurrentWorkaround;
        ReadyWithin30Days = qualification.ReadyWithin30Days;
        CampaignSource = qualification.CampaignSource;
        if (PromotionTermsVersion != promotionTermsVersion)
        {
            PromotionTermsVersion = promotionTermsVersion;
            PromotionTermsAcceptedAt = now;
        }
        return changed;
    }

    internal void ReissueToken(string token, string tokenHash, Duration tokenTtl, Instant now)
    {
        TokenHash = tokenHash;
        TokenIssuedAt = now;
        TokenExpiresAt = now + tokenTtl;
        EmitJoined(token);
    }

    internal bool IsVerified => VerifiedAt is not null;

    internal bool CanVerify(Instant now) => !IsVerified && now <= TokenExpiresAt;

    internal void Verify(Instant now)
    {
        VerifiedAt = now;
        EmitVerified(now);
    }

    internal bool TryReserveDeveloperBenefit(
        Guid userId,
        Instant publicLaunchAt,
        Instant claimDeadlineAt,
        int durationMonths,
        string requiredPromotionTermsVersion,
        Instant now)
    {
        if (!IsVerified
            || CreatedAt >= publicLaunchAt
            || now < publicLaunchAt
            || now > claimDeadlineAt
            || PromotionTermsVersion != requiredPromotionTermsVersion
            || BenefitUserId is not null)
        {
            return false;
        }

        var endsAt = now.InUtc().LocalDateTime.PlusMonths(durationMonths).InUtc().ToInstant();
        BenefitUserId = userId;
        BenefitPlan = DeveloperPlan;
        BenefitStatus = ReservedBenefitStatus;
        BenefitEligibleAt = now;
        BenefitStartsAt = now;
        BenefitEndsAt = endsAt;
        AddEvent(new WaitlistBenefitReservedEvent(Id, userId, DeveloperPlan, now, endsAt, now));
        return true;
    }

    private void EmitJoined(string token) =>
        AddOrReplaceEvent(new WaitlistJoinedEvent(
            Id,
            Email,
            Language,
            token,
            (int)(TokenExpiresAt - TokenIssuedAt).TotalHours,
            TokenIssuedAt));

    private void EmitVerified(Instant now) => AddEvent(new WaitlistVerifiedEvent(Id, now));
}
