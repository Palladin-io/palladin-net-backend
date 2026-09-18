using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Palladin.Core.Json;
using Palladin.Module.Identity.Features;
using Palladin.Module.Identity.Domain;
using Palladin.Module.Identity.Infrastructure.Persistence;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Seeders;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Identity;

[Collection<ApiFactoryCollection>]
public sealed class UserConsentTests(ApiFactory apiFactory) : TestBase
{
    private const string Path = "api/account/consents/product_analytics";
    private static UpdateUserConsentRequest Decision(bool granted, uint revision) => new()
    {
        Purpose = "product_analytics", Granted = granted, ExpectedRevision = revision,
        RequestId = Guid.NewGuid(), NoticeVersion = "2026-09-10T00:00:00Z", Locale = "en", Source = "web_settings",
    };

    [Fact]
    public async Task When_NoDecisionExists_Then_BothPurposesAreUnknownAndNotCached()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var response = await client.GetAsync("api/account/consents");
        var body = await response.Content.ReadFromJsonAsync<UserConsentsResponse>(PalladinJsonSerializationSettings.DefaultOptions);

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        body!.Consents.Length.ShouldBe(2);
        body.Consents.ShouldAllBe(consent => consent.Status == "unknown" && consent.Revision == 0);
        body.Consents.Select(consent => consent.Scope).Distinct().Count().ShouldBe(2);
        body.MaxAgeSeconds.ShouldBe(60);
    }

    [Fact]
    public async Task When_GrantIsRetriedAfterWithdrawal_Then_ItReturnsWithdrawalWithoutDuplicatingHistory()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user);
        var grant = Decision(true, 0);

        // When
        (await client.PutAsJsonAsync(Path, grant)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.PutAsJsonAsync(Path, Decision(false, 1))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var replay = await client.PutAsJsonAsync(Path, grant);
        var current = await replay.Content.ReadFromJsonAsync<UserConsentResponse>(PalladinJsonSerializationSettings.DefaultOptions);

        // Then
        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        current!.Status.ShouldBe("withdrawn");
        current.Revision.ShouldBe(2u);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        var history = await context.UserConsentHistory.Where(value => value.UserId == user.Id).OrderBy(value => value.Revision).ToListAsync();
        history.Count.ShouldBe(2);
        history[0].Status.ShouldBe("granted");
        history[1].Status.ShouldBe("withdrawn");
        history.ShouldAllBe(value => value.NoticeText == string.Empty && value.NoticeVersion == "2026-09-10T00:00:00Z" && value.Locale == "en" && value.Source == "web_settings");
        var expectedRecordedAtTicks = apiFactory.FakeClock.GetCurrentInstant().ToUnixTimeTicks();
        history[1].RecordedAt.ShouldBe(Instant.FromUnixTimeTicks(
            expectedRecordedAtTicks - expectedRecordedAtTicks % TimeSpan.TicksPerMicrosecond));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task When_DecisionsRace_Then_OneAtomicCurrentAndHistoryPairCommits(bool existing)
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user);
        if (existing)
        {
            (await client.PutAsJsonAsync(Path, Decision(true, 0))).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        var revision = existing ? 1u : 0u;

        // When
        var responses = await Task.WhenAll(client.PutAsJsonAsync(Path, Decision(true, revision)), client.PutAsJsonAsync(Path, Decision(false, revision)));

        // Then
        responses.Count(response => response.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        responses.Count(response => response.StatusCode == HttpStatusCode.Conflict).ShouldBe(1);
        var winner = await responses.Single(response => response.IsSuccessStatusCode).Content.ReadFromJsonAsync<UserConsentResponse>(PalladinJsonSerializationSettings.DefaultOptions);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbReadContext>();
        var current = await context.UserConsents.SingleAsync(value => value.UserId == user.Id);
        current.Revision.ShouldBe(revision + 1);
        current.Status.ShouldBe(winner!.Status);
        var history = await context.UserConsentHistory.Where(value => value.UserId == user.Id).ToListAsync();
        history.Count.ShouldBe((int)revision + 1);
        history.Single(value => value.Revision == current.Revision).Status.ShouldBe(current.Status);
    }

    [Fact]
    public async Task When_IdempotencyKeyIsReusedForDifferentDecision_Then_ItIsRejected()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user);
        var grant = Decision(true, 0);
        (await client.PutAsJsonAsync(Path, grant)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // When
        var changed = await client.PutAsJsonAsync(Path, grant with { Granted = false });

        // Then
        changed.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await client.GetFromJsonAsync<UserConsentsResponse>("api/account/consents", PalladinJsonSerializationSettings.DefaultOptions))!
            .Consents.Single(value => value.Purpose == "product_analytics").Status.ShouldBe("granted");
    }

    [Fact]
    public async Task When_PayloadTargetsAnotherUser_Then_OnlySessionOwnerIsChangedAndExported()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var (other, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var write = await client.PutAsJsonAsync(Path, new
        {
            granted = false, expectedRevision = 0, requestId = Guid.NewGuid(), noticeVersion = "2026-09-10T00:00:00Z",
            locale = "en", source = "web_settings", userId = other.Id,
        });
        var export = await client.GetFromJsonAsync<UserConsentHistoryResponse>($"{Path}/history?userId={other.Id}", PalladinJsonSerializationSettings.DefaultOptions);
        var otherClient = apiFactory.CreateAuthenticatedClient(other);

        // Then
        write.StatusCode.ShouldBe(HttpStatusCode.OK);
        export!.Items.Length.ShouldBe(1);
        export.Items[0].Status.ShouldBe("denied");
        (await otherClient.GetFromJsonAsync<UserConsentsResponse>("api/account/consents", PalladinJsonSerializationSettings.DefaultOptions))!.Consents
            .ShouldAllBe(consent => consent.Status == "unknown");
        (await otherClient.GetFromJsonAsync<UserConsentHistoryResponse>($"{Path}/history", PalladinJsonSerializationSettings.DefaultOptions))!.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task When_IdenticalRequestsRace_Then_OneDecisionIsRecordedAndBothReadIt()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user);
        var grant = Decision(true, 0);

        // When
        var responses = await Task.WhenAll(client.PutAsJsonAsync(Path, grant), client.PutAsJsonAsync(Path, grant));

        // Then
        responses.ShouldAllBe(response => response.StatusCode == HttpStatusCode.OK);
        var history = await client.GetFromJsonAsync<UserConsentHistoryResponse>($"{Path}/history", PalladinJsonSerializationSettings.DefaultOptions);
        history!.Items.Length.ShouldBe(1);
        history.Items[0].Revision.ShouldBe(1u);
    }

    [Fact]
    public async Task When_OldDeviceGrantsAfterWithdrawal_Then_ItCannotRestoreConsent()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user);
        (await client.PutAsJsonAsync(Path, Decision(true, 0))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.PutAsJsonAsync(Path, Decision(false, 1))).StatusCode.ShouldBe(HttpStatusCode.OK);

        // When
        var stale = await client.PutAsJsonAsync(Path, Decision(true, 1));

        // Then
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var history = await client.GetFromJsonAsync<UserConsentHistoryResponse>($"{Path}/history", PalladinJsonSerializationSettings.DefaultOptions);
        history!.Items.Length.ShouldBe(2);
        history.Items[0].Status.ShouldBe("withdrawn");
    }

    [Fact]
    public async Task When_MembershipIsBeingRemoved_Then_UserCanStillWithdrawAccountConsent()
    {
        // Given
        var (_, organization, _) = await apiFactory.Services.SeedUserAsync();
        var user = await apiFactory.Services.SeedAdditionalOrganizationMemberAsync(organization.Id);
        var client = apiFactory.CreateAuthenticatedClient(user);
        (await client.PutAsJsonAsync(Path, Decision(true, 0))).StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
        var membership = await context.OrganizationMembers.SingleAsync(value => value.UserId == user.Id && value.OrganizationId == user.OrganizationId);
        membership.RequestRemoval(Guid.NewGuid(), user.Id, apiFactory.FakeClock.GetCurrentInstant());
        await context.SaveChangesAsync();

        // When
        var response = await client.PutAsJsonAsync(Path, Decision(false, 1));

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<UserConsentResponse>(PalladinJsonSerializationSettings.DefaultOptions))!
            .Status.ShouldBe("withdrawn");
    }

    [Fact]
    public async Task When_HistoryExceedsOnePage_Then_ExportUsesRevisionCursorAndAccountRemovalCascades()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
        var consent = UserConsent.Create(user.Id, "product_analytics");
        context.UserConsents.Add(consent);
        var notice = new ConsentNotice("product_analytics", "palladin_web_mobile", "2026-09-10T00:00:00Z", "en");
        for (uint revision = 0; revision < 51; revision++)
        {
            context.UserConsentHistory.Add(consent.TryDecide(revision % 2 == 0, revision, Guid.NewGuid(), notice,
                "web_settings", apiFactory.FakeClock.GetCurrentInstant())!);
        }
        await context.SaveChangesAsync();

        // When
        var first = await client.GetFromJsonAsync<UserConsentHistoryResponse>($"{Path}/history", PalladinJsonSerializationSettings.DefaultOptions);
        var last = await client.GetFromJsonAsync<UserConsentHistoryResponse>($"{Path}/history?beforeRevision={first!.NextBeforeRevision}", PalladinJsonSerializationSettings.DefaultOptions);

        // Then
        first.Items.Length.ShouldBe(50);
        first.Items[0].Revision.ShouldBe(51u);
        first.NextBeforeRevision.ShouldBe(2u);
        last!.Items.ShouldHaveSingleItem().Revision.ShouldBe(1u);
        last.NextBeforeRevision.ShouldBeNull();
        await context.Users.Where(value => value.Id == user.Id).ExecuteDeleteAsync();
        (await context.UserConsents.AnyAsync(value => value.UserId == user.Id)).ShouldBeFalse();
        (await context.UserConsentHistory.AnyAsync(value => value.UserId == user.Id)).ShouldBeFalse();
    }

    [Fact]
    public async Task When_Unauthenticated_Then_ReadWriteAndExportAreRejected()
    {
        // Given
        var client = apiFactory.CreateClient();

        // Then
        (await client.GetAsync("api/account/consents")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.GetAsync($"{Path}/history")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.PutAsJsonAsync(Path, Decision(true, 0))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("2026-09-09T00:00:00Z")]
    [InlineData("2099-01-01T00:00:00Z")]
    public async Task When_UnpublishedVersionIsSubmitted_Then_NoDecisionOrHistoryIsWritten(string version)
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var response = await client.PutAsJsonAsync(Path, Decision(true, 0) with { NoticeVersion = version });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var history = await client.GetFromJsonAsync<UserConsentHistoryResponse>($"{Path}/history", PalladinJsonSerializationSettings.DefaultOptions);
        history!.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task When_OldNoticeIsNoLongerRegistered_Then_ItsRecordedGrantCanStillBeWithdrawn()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user);
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbWriteContext>();
        var consent = UserConsent.Create(user.Id, "product_analytics");
        context.UserConsents.Add(consent);
        context.UserConsentHistory.Add(consent.TryDecide(true, 0, Guid.NewGuid(),
            new ConsentNotice("product_analytics", "palladin_web_mobile", "old-version", "pl"),
            "mobile_settings", apiFactory.FakeClock.GetCurrentInstant())!);
        await context.SaveChangesAsync();

        // When
        var response = await client.PutAsJsonAsync(Path, Decision(false, 1) with { NoticeVersion = "old-version", Locale = "pl" });

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<UserConsentResponse>(PalladinJsonSerializationSettings.DefaultOptions);
        result!.Status.ShouldBe("withdrawn");
        result.NoticeVersion.ShouldBe("old-version");
        result.NoticeLocale.ShouldBe("pl");
    }
}
