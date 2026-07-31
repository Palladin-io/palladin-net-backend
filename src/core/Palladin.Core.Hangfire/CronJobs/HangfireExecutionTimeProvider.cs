using System.Data;
using Hangfire.PostgreSql;
using Npgsql;

namespace Palladin.Core.Hangfire.CronJobs;

internal sealed class ConnectionStringHangfireExecutionTimeProvider(string connectionString)
    : ICronJobExecutionTimeProvider
{
    public async Task<DateTime?> GetLastSuccessfulExecutionStartAsync(
        string jobName,
        CancellationToken cancellationToken
    )
    {
        await using var connection = new NpgsqlConnection(connectionString);
        if (connection.State == ConnectionState.Closed)
        {
            await connection.OpenAsync(cancellationToken);
        }

        return await HangfireExecutionTimeProviderHelpers.GetLastSuccessfulExecutionStartAsync(
            connection,
            jobName,
            cancellationToken
        );
    }
}

internal sealed class ConnectionFactoryHangfireExecutionTimeProvider : ICronJobExecutionTimeProvider
{
    private readonly IConnectionFactory _connectionFactory;

    public ConnectionFactoryHangfireExecutionTimeProvider(IConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<DateTime?> GetLastSuccessfulExecutionStartAsync(
        string jobName,
        CancellationToken cancellationToken
    )
    {
        await using var connection = _connectionFactory.GetOrCreateConnection();
        if (connection.State == ConnectionState.Closed)
        {
            await connection.OpenAsync(cancellationToken);
        }

        return await HangfireExecutionTimeProviderHelpers.GetLastSuccessfulExecutionStartAsync(
            connection,
            jobName,
            cancellationToken
        );
    }
}

internal static class HangfireExecutionTimeProviderHelpers
{
    public static async Task<DateTime?> GetLastSuccessfulExecutionStartAsync(
        NpgsqlConnection connection,
        string jobName,
        CancellationToken cancellationToken
    )
    {
        const string sqlQuery = """
                                SELECT s.createdat
                                FROM hangfire.job j
                                         LEFT JOIN hangfire.state s ON j.stateid = s.id
                                WHERE j.statename = 'Succeeded' AND j.invocationdata->>'Type' LIKE '%' || @jobName || '%'
                                ORDER BY s.createdat DESC
                                LIMIT 1
                                """;

        await using var command = new NpgsqlCommand(sqlQuery, connection);
        command.Parameters.Add(new NpgsqlParameter("jobName", jobName));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return reader.GetDateTime(0);
        }

        return null;
    }
}
