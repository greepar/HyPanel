namespace HyPanel.Server.Persistence;

using Microsoft.Data.Sqlite;

internal sealed class SqliteConnectionFactory
{
    private const string DefaultConnectionString = "Data Source=hypanel.db";
    private readonly string _connectionString;

    public SqliteConnectionFactory(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("HyPanel")
            ?? configuration["HyPanel:Database:ConnectionString"]
            ?? DefaultConnectionString;
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }
}
