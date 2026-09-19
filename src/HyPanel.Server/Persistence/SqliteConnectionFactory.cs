namespace HyPanel.Server.Persistence;

using Microsoft.Data.Sqlite;

internal sealed class SqliteConnectionFactory
{
    private readonly string _connectionString;

    public string DatabasePath { get; }

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
        var builder = new SqliteConnectionStringBuilder(_connectionString);
        if (string.IsNullOrWhiteSpace(builder.DataSource) || builder.DataSource == ":memory:")
            throw new InvalidOperationException("HyPanel backup and restore require a file-backed SQLite database.");
        DatabasePath = Path.GetFullPath(builder.DataSource);
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

    public SqliteConnection CreateConnection(string path, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate) =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(path),
            Mode = mode,
            Pooling = false
        }.ToString());
}
