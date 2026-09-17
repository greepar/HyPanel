namespace HyPanel.Server.Persistence;

using Microsoft.Data.Sqlite;

internal sealed class SqliteConnectionFactory
{
    private readonly string _connectionString;

    public SqliteConnectionFactory(IConfiguration configuration)
    {
        var configured = configuration.GetConnectionString("HyPanel")
            ?? configuration["HyPanel:Database:ConnectionString"];
        var dataDirectory = ServerDataDirectory.Resolve(configuration);
        Directory.CreateDirectory(dataDirectory);
        _connectionString = configured ?? new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataDirectory, "hypanel.db")
        }.ToString();
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
