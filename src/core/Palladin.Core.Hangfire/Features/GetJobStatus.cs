using Palladin.Core.Security;
using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Palladin.Core.Hangfire.Features;

[PublicAPI]
public sealed record GetJobStatusRequest(
    [property: FromRoute] string JobId);

[PublicAPI]
public sealed record GetJobStatusResponse(GetJobStatusResponse.JobStatus Value)
{
    public enum JobStatus
    {
        Enqueued,
        Processing,
        Scheduled,
        Succeeded,
        Failed,
        Deleted,
        AwaitingRetry,
        Awaiting,
    }
}

[UsedImplicitly]
internal sealed class GetJobStatusEndpoint(IOptions<HangfireOptions> hangfireOptions)
    : Endpoint<GetJobStatusRequest, Results<Ok<GetJobStatusResponse>, NotFound, BadRequest>>
{
    public override void Configure()
    {
        // Outside the public /api prefix so the edge (nginx) can keep Hangfire internal-only.
        Get("hangfire/jobs/{jobId}/status");
        this.RequirePermission(Permission.OrganizationManagement);
        Summary(summary => { summary.Summary = "Gets job status"; });
        Description(d => d.WithTags("Hangfire"));
    }

    public override async Task<Results<Ok<GetJobStatusResponse>, NotFound, BadRequest>> ExecuteAsync(
        GetJobStatusRequest request,
        CancellationToken cancellationToken)
    {
        if (!int.TryParse(request.JobId, out var jobId))
        {
            return TypedResults.BadRequest();
        }

        const string sqlQuery = """
                                SELECT "statename"
                                FROM "hangfire"."job"
                                WHERE "id" = @Id
                                LIMIT 1
                                """;
        await using var connection = new NpgsqlConnection(hangfireOptions.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sqlQuery, connection);
        command.Parameters.AddWithValue("Id", jobId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            var state = reader.GetString(0);
            var tryParse = Enum.TryParse<GetJobStatusResponse.JobStatus>(state, out var jobStatus);
            var response = new GetJobStatusResponse(tryParse ? jobStatus : GetJobStatusResponse.JobStatus.Failed);
            return TypedResults.Ok(response);
        }

        return TypedResults.NotFound();
    }
}
