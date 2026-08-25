using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Features;
using Palladin.Module.Identity.Infrastructure.Jwt;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using FastEndpoints;
using Microsoft.AspNetCore.Http;
using FastEndpoints.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NSubstitute;
using Shouldly;
using System.Net;
using System.Net.Http.Json;

namespace Palladin.Tests.Integrations.Features.Identity;

[Collection<ApiFactoryCollection>]
public sealed class WaitlistTests(ApiFactory apiFactory) : TestBase
{
    [Fact]
    public async Task When_JoiningWaitlist_Then_PendingEntryCreated()
    {
        // Given
        var email = $"waitlist-{Guid.NewGuid():N}@example.com";
        apiFactory.GuidProvider.Generate().Returns(_ => Guid.NewGuid());
        var client = apiFactory.CreateClient();

        // When
        var (response, result) = await client.POSTAsync<JoinWaitlistEndpoint, JoinWaitlistRequest, JoinWaitlistResponse>(
            NewJoinRequest($"  {email.ToUpperInvariant()}  ", "pl"));

        // Then
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Accepted);
        result.Status.ShouldBe("accepted");

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var entry = await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>()
            .WaitlistEntries.SingleAsync(x => x.Email == email, TestContext.Current.CancellationToken);
        entry.VerifiedAt.ShouldBeNull();
        entry.Language.ShouldBe("pl");
        entry.AudienceType.ShouldBe("individual");
        entry.AgentFramework.ShouldBe("codex");
        entry.CredentialedWorkflow.ShouldBe("Rotate API credentials for release automation");
        entry.CurrentWorkaround.ShouldBe("Encrypted notes and manual copy-paste");
        entry.ReadyWithin30Days.ShouldBe(true);
        entry.CampaignSource.ShouldBe("direct");
        entry.TokenHash.ShouldNotBeNullOrWhiteSpace();
        entry.TokenExpiresAt.ShouldBeGreaterThan(entry.TokenIssuedAt);
    }

    [Fact]
    public async Task When_UpdatingQualificationWithinCooldown_Then_AnswersChangeAndTokenStaysUnchanged()
    {
        // Given
        var email = $"waitlist-{Guid.NewGuid():N}@example.com";
        apiFactory.GuidProvider.Generate().Returns(_ => Guid.NewGuid());
        var client = apiFactory.CreateClient();
        await client.POSTAsync<JoinWaitlistEndpoint, JoinWaitlistRequest, JoinWaitlistResponse>(
            NewJoinRequest(email));
        var firstHash = await ReadTokenHashAsync(email);

        // When
        var (response, _) = await client.POSTAsync<JoinWaitlistEndpoint, JoinWaitlistRequest, JoinWaitlistResponse>(
            NewJoinRequest(email) with
            {
                AudienceType = "team",
                AgentFramework = "other",
                AgentFrameworkOther = "  Internal MCP client  ",
                CredentialedWorkflow = "  Run a credentialed deployment  ",
                CurrentWorkaround = null,
                ReadyWithin30Days = false,
                CampaignSource = "github",
            });

        // Then
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Accepted);
        (await ReadTokenHashAsync(email)).ShouldBe(firstHash);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var entry = await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>()
            .WaitlistEntries.SingleAsync(x => x.Email == email, TestContext.Current.CancellationToken);
        entry.AudienceType.ShouldBe("team");
        entry.AgentFramework.ShouldBe("other");
        entry.AgentFrameworkOther.ShouldBe("Internal MCP client");
        entry.CredentialedWorkflow.ShouldBe("Run a credentialed deployment");
        entry.CurrentWorkaround.ShouldBeNull();
        entry.ReadyWithin30Days.ShouldBe(false);
        entry.CampaignSource.ShouldBe("github");
    }

    [Fact]
    public async Task When_ConcurrentRequestsJoinWithTheSameEmail_Then_AllAreAcceptedAndOnlyOneEntryExists()
    {
        // Given
        var email = $"parallel-waitlist-{Guid.NewGuid():N}@example.com";
        apiFactory.GuidProvider.Generate().Returns(_ => Guid.NewGuid());
        var clients = Enumerable.Range(0, 8).Select(_ => apiFactory.CreateClient()).ToArray();

        // When
        var responses = await Task.WhenAll(clients.Select(client => client.PostAsJsonAsync(
            "api/waitlist", NewJoinRequest(email), TestContext.Current.CancellationToken)));

        // Then
        responses.ShouldAllBe(response => response.StatusCode == HttpStatusCode.Accepted);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>()
                .WaitlistEntries.CountAsync(x => x.Email == email, TestContext.Current.CancellationToken))
            .ShouldBe(1);
    }

    [Fact]
    public async Task When_VerifyingWithValidToken_Then_EntryVerifiedAndRedirected()
    {
        // Given
        var token = $"token-{Guid.NewGuid():N}";
        var entryId = await SeedEntryAsync(token, apiFactory.FakeClock.GetCurrentInstant());
        var client = apiFactory.CreateClient(new ClientOptions { AllowAutoRedirect = false });

        // When
        var response = await client.GetAsync($"api/waitlist/verify?token={token}", TestContext.Current.CancellationToken);

        // Then
        ((int)response.StatusCode).ShouldBe(StatusCodes.Status302Found);
        response.Headers.Location!.ToString().ShouldBe("https://palladin.io/waitlist/confirmed");

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var entry = await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>()
            .WaitlistEntries.SingleAsync(x => x.Id == entryId, TestContext.Current.CancellationToken);
        entry.VerifiedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task When_VerifyingWithUnknownToken_Then_RedirectedToFailure()
    {
        // Given
        var client = apiFactory.CreateClient(new ClientOptions { AllowAutoRedirect = false });

        // When
        var response = await client.GetAsync(
            $"api/waitlist/verify?token=unknown-{Guid.NewGuid():N}", TestContext.Current.CancellationToken);

        // Then
        ((int)response.StatusCode).ShouldBe(StatusCodes.Status302Found);
        response.Headers.Location!.ToString().ShouldBe("https://palladin.io/waitlist/invalid");
    }

    [Fact]
    public async Task When_VerifyingWithExpiredToken_Then_RedirectedToFailure()
    {
        // Given — entry issued 48h ago with a 24h TTL
        var token = $"token-{Guid.NewGuid():N}";
        var issuedAt = apiFactory.FakeClock.GetCurrentInstant() - Duration.FromHours(48);
        await SeedEntryAsync(token, issuedAt);
        var client = apiFactory.CreateClient(new ClientOptions { AllowAutoRedirect = false });

        // When
        var response = await client.GetAsync($"api/waitlist/verify?token={token}", TestContext.Current.CancellationToken);

        // Then
        ((int)response.StatusCode).ShouldBe(StatusCodes.Status302Found);
        response.Headers.Location!.ToString().ShouldBe("https://palladin.io/waitlist/invalid");
    }

    [Fact]
    public async Task When_VerifyingAnAlreadyVerifiedEntry_Then_RedirectedToAlreadyConfirmed()
    {
        // Given
        var token = $"token-{Guid.NewGuid():N}";
        await SeedEntryAsync(token, apiFactory.FakeClock.GetCurrentInstant(), verified: true);
        var client = apiFactory.CreateClient(new ClientOptions { AllowAutoRedirect = false });

        // When
        var response = await client.GetAsync($"api/waitlist/verify?token={token}", TestContext.Current.CancellationToken);

        // Then
        ((int)response.StatusCode).ShouldBe(StatusCodes.Status302Found);
        response.Headers.Location!.ToString().ShouldBe("https://palladin.io/waitlist/already-confirmed");
    }

    [Fact]
    public async Task When_RequestingPublicConfigurationBeforeLaunch_Then_ExactDatesAndTermsVersionAreReturned()
    {
        // Given
        var originalNow = apiFactory.FakeClock.GetCurrentInstant();
        apiFactory.FakeClock.Reset(Instant.FromUtc(2026, 11, 30, 23, 0));

        try
        {
            // When
            var result = await apiFactory.CreateClient().GetFromJsonAsync<GetWaitlistConfigResponse>(
                "api/waitlist/config", TestContext.Current.CancellationToken);

            // Then
            result.ShouldNotBeNull();
            result.AcceptingSignups.ShouldBeTrue();
            result.PublicLaunchAtUtc.ShouldBe(DateTimeOffset.Parse("2026-12-01T00:00:00Z"));
            result.BenefitClaimDeadlineAtUtc.ShouldBe(DateTimeOffset.Parse("2027-06-01T00:00:00Z"));
            result.PromotionTermsVersion.ShouldBe(WaitlistEntry.CurrentPromotionTermsVersion);
        }
        finally
        {
            apiFactory.FakeClock.Reset(originalNow);
        }
    }

    [Fact]
    public async Task When_SubmittingAfterPublicLaunch_Then_WaitlistIsClosed()
    {
        // Given
        var originalNow = apiFactory.FakeClock.GetCurrentInstant();
        apiFactory.FakeClock.Reset(Instant.FromUtc(2026, 12, 1, 0, 0));
        var email = $"after-launch-{Guid.NewGuid():N}@example.com";

        try
        {
            // When
            var response = await apiFactory.CreateClient().PostAsJsonAsync(
                "api/waitlist", NewJoinRequest(email), TestContext.Current.CancellationToken);

            // Then
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            await using var scope = apiFactory.Services.CreateAsyncScope();
            (await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>()
                    .WaitlistEntries.AnyAsync(x => x.Email == email, TestContext.Current.CancellationToken))
                .ShouldBeFalse();
        }
        finally
        {
            apiFactory.FakeClock.Reset(originalNow);
        }
    }

    [Fact]
    public async Task When_ConfirmationOccursAfterPublicLaunch_Then_ValidTokenStillConfirmsTheSignup()
    {
        // Given
        var originalNow = apiFactory.FakeClock.GetCurrentInstant();
        var token = $"post-launch-token-{Guid.NewGuid():N}";
        var entryId = await SeedEntryAsync(token, Instant.FromUtc(2026, 11, 30, 23, 30));
        apiFactory.FakeClock.Reset(Instant.FromUtc(2026, 12, 1, 0, 15));

        try
        {
            // When
            var response = await apiFactory.CreateClient(new ClientOptions { AllowAutoRedirect = false })
                .GetAsync($"api/waitlist/verify?token={token}", TestContext.Current.CancellationToken);

            // Then
            ((int)response.StatusCode).ShouldBe(StatusCodes.Status302Found);
            response.Headers.Location!.ToString().ShouldBe("https://palladin.io/waitlist/confirmed");
            await using var scope = apiFactory.Services.CreateAsyncScope();
            (await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>()
                    .WaitlistEntries.SingleAsync(x => x.Id == entryId, TestContext.Current.CancellationToken))
                .VerifiedAt.ShouldBe(Instant.FromUtc(2026, 12, 1, 0, 15));
        }
        finally
        {
            apiFactory.FakeClock.Reset(originalNow);
        }
    }

    [Theory]
    [InlineData("individual")]
    [InlineData("team")]
    public async Task When_JoiningFromAnAudienceSegment_Then_SegmentIsStored(string audienceType)
    {
        // Given
        var email = $"segment-{Guid.NewGuid():N}@example.com";
        apiFactory.GuidProvider.Generate().Returns(_ => Guid.NewGuid());
        var client = apiFactory.CreateClient();

        // When
        var (response, _) = await client.POSTAsync<JoinWaitlistEndpoint, JoinWaitlistRequest, JoinWaitlistResponse>(
            NewJoinRequest(email) with { AudienceType = audienceType });

        // Then
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Accepted);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>()
                .WaitlistEntries.SingleAsync(x => x.Email == email, TestContext.Current.CancellationToken))
            .AudienceType.ShouldBe(audienceType);
    }

    [Fact]
    public async Task When_QualificationContainsUnicodeAndMarkup_Then_ItIsStoredAsPlainData()
    {
        // Given
        var email = $"unicode-qualification-{Guid.NewGuid():N}@example.com";
        const string workflow = "Rotate klucz 🔐 for <script>alert('x')</script> deployment";
        const string workaround = "Ręczne kopiowanie & review <b>przez człowieka</b>";

        // When
        var response = await apiFactory.CreateClient().PostAsJsonAsync(
            "api/waitlist",
            NewJoinRequest(email) with
            {
                CredentialedWorkflow = workflow,
                CurrentWorkaround = workaround,
            },
            TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var entry = await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>()
            .WaitlistEntries.SingleAsync(x => x.Email == email, TestContext.Current.CancellationToken);
        entry.CredentialedWorkflow.ShouldBe(workflow);
        entry.CurrentWorkaround.ShouldBe(workaround);
    }

    [Fact]
    public async Task When_FreeTextExceedsItsLimit_Then_RequestIsRejected()
    {
        // Given
        var requests = new[]
        {
            NewJoinRequest($"long-other-{Guid.NewGuid():N}@example.com") with
            {
                AgentFramework = "other",
                AgentFrameworkOther = new string('a', WaitlistQualification.AgentFrameworkOtherMaxLength + 1),
            },
            NewJoinRequest($"long-workflow-{Guid.NewGuid():N}@example.com") with
            {
                CredentialedWorkflow = new string('a', WaitlistQualification.CredentialedWorkflowMaxLength + 1),
            },
            NewJoinRequest($"long-workaround-{Guid.NewGuid():N}@example.com") with
            {
                CurrentWorkaround = new string('a', WaitlistQualification.CurrentWorkaroundMaxLength + 1),
            },
        };
        var client = apiFactory.CreateClient();

        // When
        var responses = new List<HttpResponseMessage>();
        foreach (var request in requests)
        {
            responses.Add(await client.PostAsJsonAsync(
                "api/waitlist", request, TestContext.Current.CancellationToken));
        }

        // Then
        responses.ShouldAllBe(response => response.StatusCode == HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("unknown", "codex", null, "Rotate a credential", true, "direct")]
    [InlineData("individual", "unknown", null, "Rotate a credential", true, "direct")]
    [InlineData("individual", "other", null, "Rotate a credential", true, "direct")]
    [InlineData("individual", "codex", "Unexpected other value", "Rotate a credential", true, "direct")]
    [InlineData("individual", "codex", null, "   ", true, "direct")]
    [InlineData("individual", "codex", null, "Rotate a credential", null, "direct")]
    [InlineData("individual", "codex", null, "Rotate a credential", true, "untrusted-source")]
    public async Task When_QualificationPayloadIsInvalid_Then_RequestIsRejectedWithoutCreatingAnEntry(
        string audienceType,
        string agentFramework,
        string? agentFrameworkOther,
        string workflow,
        bool? readyWithin30Days,
        string campaignSource)
    {
        // Given
        var email = $"invalid-qualification-{Guid.NewGuid():N}@example.com";
        var request = NewJoinRequest(email) with
        {
            AudienceType = audienceType,
            AgentFramework = agentFramework,
            AgentFrameworkOther = agentFrameworkOther,
            CredentialedWorkflow = workflow,
            ReadyWithin30Days = readyWithin30Days,
            CampaignSource = campaignSource,
        };

        // When
        var response = await apiFactory.CreateClient().PostAsJsonAsync(
            "api/waitlist", request, TestContext.Current.CancellationToken);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>()
                .WaitlistEntries.AnyAsync(x => x.Email == email, TestContext.Current.CancellationToken))
            .ShouldBeFalse();
    }

    private async Task<Guid> SeedEntryAsync(string token, Instant issuedAt, bool verified = false)
    {
        var entry = WaitlistEntry.Join(
            Guid.NewGuid(), $"seed-{Guid.NewGuid():N}@example.com", "en",
            WaitlistQualification.Create(
                "individual",
                "codex",
                null,
                "Rotate API credentials for release automation",
                null,
                true,
                "direct"),
            WaitlistEntry.CurrentPromotionTermsVersion,
            token, TokenService.HashToken(token), Duration.FromHours(24), issuedAt);
        if (verified)
        {
            entry.Verify(issuedAt + Duration.FromMinutes(1));
        }

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
        writeContext.WaitlistEntries.Add(entry);
        await writeContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        return entry.Id;
    }

    private async Task<string> ReadTokenHashAsync(string email)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>()
            .WaitlistEntries.Where(x => x.Email == email)
            .Select(x => x.TokenHash)
            .SingleAsync(TestContext.Current.CancellationToken);
    }

    private static JoinWaitlistRequest NewJoinRequest(string email, string language = "en") => new()
    {
        Email = email,
        Language = language,
        AudienceType = "individual",
        AgentFramework = "codex",
        CredentialedWorkflow = "  Rotate API credentials for release automation  ",
        CurrentWorkaround = "  Encrypted notes and manual copy-paste  ",
        ReadyWithin30Days = true,
        CampaignSource = "direct",
        PromotionTermsVersion = WaitlistEntry.CurrentPromotionTermsVersion,
    };
}
