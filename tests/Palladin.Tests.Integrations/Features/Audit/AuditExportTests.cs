using System.Net;
using System.Net.Http.Json;
using System.Text;
using Palladin.Module.Audit.Infrastructure.Exports;
using Palladin.Module.Audit.Domain;
using Palladin.Module.Audit.Features.Export;
using Palladin.Module.Audit.Infrastructure.Persistence;
using Palladin.Module.Identity.Contracts.ValueObjects;
using Palladin.Tests.Integrations.Shared;
using Palladin.Tests.Integrations.Shared.Extensions;
using Palladin.Tests.Integrations.Shared.Seeders;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Shouldly;

namespace Palladin.Tests.Integrations.Features.Audit;

[Collection<ApiFactoryCollection>]
public sealed class AuditExportTests(ApiFactory apiFactory) : TestBase
{
    private sealed class RecordingCdnService : IAuditExportStorage
    {
        public string? LastCsv { get; private set; }
        public string? FailureMessage { get; init; }

        public async Task UploadAsync(Stream inputStream, string destinationPath, CancellationToken cancellationToken)
        {
            if (FailureMessage is not null)
            {
                throw new InvalidOperationException(FailureMessage);
            }

            using var reader = new StreamReader(inputStream, Encoding.UTF8);
            LastCsv = await reader.ReadToEndAsync(cancellationToken);
        }

        public Task<string> CreateDownloadUrlAsync(string documentName, CancellationToken cancellationToken) =>
            Task.FromResult($"https://cdn.test/{documentName}");
    }

    private async Task SeedAuditRowsAsync(Guid orgId, Guid vaultId, int count)
    {
        for (var i = 0; i < count; i++)
        {
            await AuditSeeding.SeedGrantApprovedAsync(apiFactory, orgId, vaultId, Guid.NewGuid());
        }
    }

    private Task SeedRowAsync(Guid orgId, string eventType, Guid? agentId = null, Guid? userId = null) =>
        AuditSeeding.SeedRowAsync(apiFactory, orgId, eventType, agentId, userId);

    private async Task<Guid> InsertJobAsync(
        Guid orgId,
        Action<AuditExportJob>? mutate = null,
        string? vaultIds = null,
        string? agentIds = null,
        string? userIds = null,
        Guid? entryId = null,
        string? eventTypes = null)
    {
        await using var scope = apiFactory.Services.CreateAsyncScope();
        var writeContext = scope.ServiceProvider.GetRequiredService<AuditDbWriteContext>();
        var job = AuditExportJob.Create(Guid.NewGuid(), orgId, Guid.NewGuid(), "Requester", "Pro", "none",
            vaultIds, agentIds, userIds, entryId, eventTypes, null, null, apiFactory.FakeClock.GetCurrentInstant());
        mutate?.Invoke(job);
        writeContext.AuditExportJobs.Add(job);
        await writeContext.SaveChangesAsync();
        return job.Id;
    }

    [Fact]
    public async Task When_FreePlanRequestsExport_Then_Returns403PlanUpgradeRequired()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user, plan: PlanType.Basic);

        // When
        var response = await client.PostAsJsonAsync("api/audit-logs/export", new RequestAuditLogExportRequest());

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).ShouldContain("plan-upgrade-required");
    }

    [Fact]
    public async Task When_ProPlanRequestsExport_Then_Accepts202AndPersistsPendingJob()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user, plan: PlanType.Pro);

        // When
        var response = await client.PostAsJsonAsync("api/audit-logs/export", new RequestAuditLogExportRequest());

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var body = await response.Content.ReadFromJsonAsync<RequestAuditLogExportResponse>();
        body.ShouldNotBeNull();

        await using var scope = apiFactory.Services.CreateAsyncScope();
        var readContext = scope.ServiceProvider.GetRequiredService<AuditDbReadContext>();
        var job = await readContext.AuditExportJobs.FirstOrDefaultAsync(j => j.Id == body.JobId);
        job.ShouldNotBeNull();
        job.OrganizationId.ShouldBe(organization.Id);
        job.Status.ShouldBe(AuditExportStatus.Pending);
    }

    [Fact]
    public async Task When_RunnerGeneratesCsv_Then_JobCompletedAndCsvHasRowsWithoutSecrets()
    {
        // Given
        var orgId = Guid.NewGuid();
        await SeedAuditRowsAsync(orgId, Guid.NewGuid(), 5);
        var jobId = await InsertJobAsync(orgId);
        var cdn = new RecordingCdnService();

        // When
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var runner = ActivatorUtilities.CreateInstance<AuditExportJobRunner>(scope.ServiceProvider, cdn);
            await runner.RunAsync(jobId, CancellationToken.None);
        }

        // Then
        await using var verifyScope = apiFactory.Services.CreateAsyncScope();
        var readContext = verifyScope.ServiceProvider.GetRequiredService<AuditDbReadContext>();
        var job = await readContext.AuditExportJobs.FirstAsync(j => j.Id == jobId);
        job.Status.ShouldBe(AuditExportStatus.Completed);
        job.RowCount.ShouldBe(5);
        job.ExpiresAt.ShouldBe(job.CompletedAt!.Value.Plus(Duration.FromHours(24)));

        cdn.LastCsv.ShouldNotBeNull();
        var lines = cdn.LastCsv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(6); // header + 5 rows
        lines[0].ShouldBe("occurredAt,eventType,actorType,result,actorName,agentName,userId,agentId,vaultId,entryId,metadata");
        cdn.LastCsv.ShouldContain("grant.approved");
        cdn.LastCsv.ShouldNotContain("pl_");
        cdn.LastCsv.ShouldNotContain("reEncryptedBlob");
    }

    [Fact]
    public async Task When_ExportFails_Then_SensitiveExceptionMessageIsNotPersisted()
    {
        // Given
        const string sensitiveMessage = "entry-name-and-request-reason";
        var orgId = Guid.NewGuid();
        await SeedAuditRowsAsync(orgId, Guid.NewGuid(), 1);
        var jobId = await InsertJobAsync(orgId);
        var cdn = new RecordingCdnService { FailureMessage = sensitiveMessage };

        // When
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var runner = ActivatorUtilities.CreateInstance<AuditExportJobRunner>(scope.ServiceProvider, cdn);
            await Should.ThrowAsync<InvalidOperationException>(() => runner.RunAsync(jobId, CancellationToken.None));
        }

        // Then
        await using var verifyScope = apiFactory.Services.CreateAsyncScope();
        var job = await verifyScope.ServiceProvider.GetRequiredService<AuditDbReadContext>()
            .AuditExportJobs.FirstAsync(j => j.Id == jobId);
        job.Status.ShouldBe(AuditExportStatus.Failed);
        job.Error.ShouldBe("export_failed");
        job.Error.ShouldNotContain(sensitiveMessage);
    }

    [Fact]
    public async Task When_RunnerHasMultiSelectFilters_Then_CsvContainsOnlyMatchingRows()
    {
        // Given
        var orgId = Guid.NewGuid();
        var agentA = Guid.NewGuid();
        var agentB = Guid.NewGuid();
        var agentC = Guid.NewGuid();
        await SeedRowAsync(orgId, "credential.accessed", agentId: agentA);
        await SeedRowAsync(orgId, "grant.requested", agentId: agentB);
        await SeedRowAsync(orgId, "credential.accessed", agentId: agentC); // excluded: agent not in filter
        await SeedRowAsync(orgId, "agent.enrolled", agentId: agentA); // excluded: eventType not in filter
        var jobId = await InsertJobAsync(orgId,
            agentIds: $"{agentA},{agentB}",
            eventTypes: "credential.accessed,grant.requested");
        var cdn = new RecordingCdnService();

        // When
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var runner = ActivatorUtilities.CreateInstance<AuditExportJobRunner>(scope.ServiceProvider, cdn);
            await runner.RunAsync(jobId, CancellationToken.None);
        }

        // Then
        await using var verifyScope = apiFactory.Services.CreateAsyncScope();
        var job = await verifyScope.ServiceProvider.GetRequiredService<AuditDbReadContext>()
            .AuditExportJobs.FirstAsync(j => j.Id == jobId);
        job.RowCount.ShouldBe(2);
        cdn.LastCsv!.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(3); // header + 2 rows
    }

    [Fact]
    public async Task When_RunnerHasUserIdFilter_Then_CsvContainsOnlyThatUsersRows()
    {
        // Given
        var orgId = Guid.NewGuid();
        var user1 = Guid.NewGuid();
        var user2 = Guid.NewGuid();
        await SeedRowAsync(orgId, "vault.created", userId: user1);
        await SeedRowAsync(orgId, "vault.created", userId: user2);
        var jobId = await InsertJobAsync(orgId, userIds: user1.ToString());
        var cdn = new RecordingCdnService();

        // When
        await using (var scope = apiFactory.Services.CreateAsyncScope())
        {
            var runner = ActivatorUtilities.CreateInstance<AuditExportJobRunner>(scope.ServiceProvider, cdn);
            await runner.RunAsync(jobId, CancellationToken.None);
        }

        // Then
        await using var verifyScope = apiFactory.Services.CreateAsyncScope();
        var job = await verifyScope.ServiceProvider.GetRequiredService<AuditDbReadContext>()
            .AuditExportJobs.FirstAsync(j => j.Id == jobId);
        job.RowCount.ShouldBe(1);
    }

    [Fact]
    public async Task When_JobCompleted_Then_StatusIsDownloadable_UntilExpiry()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user);
        var now = apiFactory.FakeClock.GetCurrentInstant();
        var jobId = await InsertJobAsync(organization.Id, j => j.MarkCompleted("audit-exports/key.csv", 3, now, Duration.FromHours(24)));

        // When
        var (firstResponse, firstResult) = await client
            .GETAsync<GetAuditLogExportEndpoint, GetAuditLogExportRequest, GetAuditLogExportResponse>(
                new GetAuditLogExportRequest { JobId = jobId });

        // Then
        firstResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        firstResult!.Status.ShouldBe("Completed");
        firstResult.Downloadable.ShouldBeTrue();

        // When — 25h later the link is expired
        apiFactory.FakeClock.Advance(Duration.FromHours(25));
        try
        {
            var (_, expiredResult) = await client
                .GETAsync<GetAuditLogExportEndpoint, GetAuditLogExportRequest, GetAuditLogExportResponse>(
                    new GetAuditLogExportRequest { JobId = jobId });

            // Then
            expiredResult!.Downloadable.ShouldBeFalse();
        }
        finally
        {
            apiFactory.FakeClock.Reset(now);
        }
    }

    [Fact]
    public async Task When_DownloadingExpiredExport_Then_Returns410()
    {
        // Given
        var (user, organization, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user);
        var now = apiFactory.FakeClock.GetCurrentInstant();
        var jobId = await InsertJobAsync(organization.Id, j => j.MarkCompleted("audit-exports/key.csv", 3, now, Duration.FromHours(24)));

        // When
        apiFactory.FakeClock.Advance(Duration.FromHours(25));
        try
        {
            var response = await client.GetAsync($"api/audit-logs/export/{jobId}/download");

            // Then
            response.StatusCode.ShouldBe(HttpStatusCode.Gone);
        }
        finally
        {
            apiFactory.FakeClock.Reset(now);
        }
    }

    [Fact]
    public async Task When_DownloadingUnknownExport_Then_Returns404()
    {
        // Given
        var (user, _, _) = await apiFactory.Services.SeedUserAsync();
        var client = apiFactory.CreateAuthenticatedClient(user);

        // When
        var response = await client.GetAsync($"api/audit-logs/export/{Guid.NewGuid()}/download");

        // Then
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
