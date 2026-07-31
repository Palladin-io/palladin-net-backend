using Palladin.Core.Api;
using Microsoft.Extensions.Options;
using Npgsql;
using Serilog;

namespace Palladin.Core.Hangfire.Persistence;

[UsedImplicitly]
internal sealed class DatabaseCreatorApplicationStartingHook(
    IOptions<HangfireOptions> hangfireOptions,
    ILogger logger
) : IApplicationStartingHook
{
    public async Task OnApplicationStartingAsync(CancellationToken cancellationToken)
    {
        var connectionStringBuilder = new NpgsqlConnectionStringBuilder(hangfireOptions.Value.ConnectionString);
        var databaseName = connectionStringBuilder.Database;
        connectionStringBuilder.Database = "postgres";
        await using var connection = new NpgsqlConnection(connectionStringBuilder.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var queryCmd = new NpgsqlCommand(
            "SELECT datname FROM pg_database WHERE datname = @databaseName",
            connection
        );
        queryCmd.Parameters.Add(new NpgsqlParameter("databaseName", databaseName));

        var queryResult = await queryCmd.ExecuteScalarAsync(cancellationToken);

        logger.Debug("The database {DatabaseName} is {Status}", databaseName, queryResult is null ? "not created" : "already created");

        if (queryResult is null)
        {
            logger.Debug("Creating database {DatabaseName}", databaseName);
            var sanitizedName = new NpgsqlConnectionStringBuilder { Database = databaseName }.Database;
            await using var createCmd = new NpgsqlCommand($"CREATE DATABASE \"{sanitizedName}\"", connection);
            await createCmd.ExecuteNonQueryAsync(cancellationToken);
            logger.Debug("Creating database {DatabaseName} is finished", databaseName);
        }
    }
}
