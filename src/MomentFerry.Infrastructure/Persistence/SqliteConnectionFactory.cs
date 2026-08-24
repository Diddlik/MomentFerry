using Microsoft.Data.Sqlite;

namespace MomentFerry.Infrastructure.Persistence;

public sealed class SqliteConnectionFactory
{
    private readonly string _connectionString;

    /// <summary>Where the database lives, for maintenance that works on the file rather than on rows.</summary>
    public string DatabasePath { get; }

    public SqliteConnectionFactory(string databasePath)
    {
        var fullPath = System.IO.Path.GetFullPath(databasePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
        DatabasePath = fullPath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        await command.ExecuteNonQueryAsync(cancellationToken);

        return connection;
    }
}
