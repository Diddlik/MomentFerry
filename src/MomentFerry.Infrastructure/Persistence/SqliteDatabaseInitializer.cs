using MomentFerry.Application.Abstractions;

namespace MomentFerry.Infrastructure.Persistence;

public sealed class SqliteDatabaseInitializer(SqliteConnectionFactory connectionFactory) : IDatabaseInitializer
{
    public const int CurrentSchemaVersion = 3;

    private static readonly IReadOnlyList<SqliteMigration> Migrations =
    [
        new SqliteMigration(
            1,
            "initial-schema",
            """
            CREATE TABLE IF NOT EXISTS shares (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                path TEXT NOT NULL,
                role INTEGER NOT NULL,
                enabled INTEGER NOT NULL,
                owner TEXT NULL,
                group_name TEXT NULL,
                preset TEXT NULL,
                stability_seconds INTEGER NOT NULL,
                recursive INTEGER NOT NULL,
                default_timezone TEXT NULL,
                ignore_patterns_json TEXT NOT NULL,
                allowed_media_types_json TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ix_shares_path ON shares(path);

            CREATE TABLE IF NOT EXISTS source_groups (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS source_group_members (
                group_id TEXT NOT NULL,
                share_id TEXT NOT NULL,
                PRIMARY KEY (group_id, share_id),
                FOREIGN KEY (group_id) REFERENCES source_groups(id) ON DELETE CASCADE,
                FOREIGN KEY (share_id) REFERENCES shares(id) ON DELETE RESTRICT
            );

            CREATE TABLE IF NOT EXISTS events (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                type TEXT NULL,
                start_at_utc TEXT NOT NULL,
                end_at_utc TEXT NULL,
                status INTEGER NOT NULL,
                source_group_id TEXT NOT NULL,
                destination_share_id TEXT NOT NULL,
                destination_folder_template TEXT NOT NULL,
                operation_mode INTEGER NOT NULL,
                conflict_strategy INTEGER NOT NULL,
                duplicate_strategy INTEGER NOT NULL,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                FOREIGN KEY (source_group_id) REFERENCES source_groups(id) ON DELETE RESTRICT,
                FOREIGN KEY (destination_share_id) REFERENCES shares(id) ON DELETE RESTRICT
            );

            CREATE INDEX IF NOT EXISTS ix_events_window ON events(start_at_utc, end_at_utc, status);

            CREATE TABLE IF NOT EXISTS media_files (
                id TEXT PRIMARY KEY,
                source_share_id TEXT NOT NULL,
                source_path TEXT NOT NULL,
                original_name TEXT NOT NULL,
                size INTEGER NOT NULL,
                extension TEXT NOT NULL,
                media_type INTEGER NOT NULL,
                captured_at_utc TEXT NULL,
                timestamp_source TEXT NULL,
                timezone_inferred INTEGER NOT NULL,
                sha256 TEXT NULL,
                first_seen_at_utc TEXT NOT NULL,
                last_seen_at_utc TEXT NOT NULL,
                FOREIGN KEY (source_share_id) REFERENCES shares(id) ON DELETE CASCADE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ix_media_files_source ON media_files(source_share_id, source_path);
            CREATE INDEX IF NOT EXISTS ix_media_files_captured_at ON media_files(captured_at_utc);

            CREATE TABLE IF NOT EXISTS operations (
                id TEXT PRIMARY KEY,
                media_file_id TEXT NOT NULL,
                event_id TEXT NULL,
                state INTEGER NOT NULL,
                source_path TEXT NOT NULL,
                staging_path TEXT NULL,
                destination_path TEXT NULL,
                source_hash TEXT NULL,
                destination_hash TEXT NULL,
                retry_count INTEGER NOT NULL,
                last_error TEXT NULL,
                started_at_utc TEXT NOT NULL,
                completed_at_utc TEXT NULL,
                updated_at_utc TEXT NOT NULL,
                FOREIGN KEY (media_file_id) REFERENCES media_files(id) ON DELETE CASCADE,
                FOREIGN KEY (event_id) REFERENCES events(id) ON DELETE SET NULL
            );

            CREATE INDEX IF NOT EXISTS ix_operations_media_state ON operations(media_file_id, state);
            CREATE INDEX IF NOT EXISTS ix_operations_updated ON operations(updated_at_utc DESC);
            """),
        new SqliteMigration(
            2,
            "media-source-last-write",
            """
            ALTER TABLE media_files ADD COLUMN source_last_write_at_utc TEXT NULL;
            """),
        new SqliteMigration(
            3,
            "share-extensions-and-destination-subfolders",
            """
            ALTER TABLE shares ADD COLUMN image_extensions_json TEXT NULL;
            ALTER TABLE shares ADD COLUMN video_extensions_json TEXT NULL;
            ALTER TABLE shares ADD COLUMN image_subfolder TEXT NULL;
            ALTER TABLE shares ADD COLUMN video_subfolder TEXT NULL;
            """)
    ];

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ValidateMigrationList();

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await EnableWalAsync(connection, cancellationToken);
        await EnsureMigrationTableAsync(connection, cancellationToken);

        var applied = await GetAppliedVersionsAsync(connection, cancellationToken);
        if (applied.Count > 0 && applied.Max() > CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Database schema version {applied.Max()} is newer than this MomentFerry build supports " +
                $"(maximum {CurrentSchemaVersion}). Refusing to start with an older application version.");
        }

        foreach (var migration in Migrations.OrderBy(x => x.Version))
        {
            if (applied.Contains(migration.Version)) continue;
            await ApplyMigrationAsync(connection, migration, cancellationToken);
        }
    }

    private static async Task EnableWalAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL;";
        await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task EnsureMigrationTableAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                applied_at_utc TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<HashSet<int>> GetAppliedVersionsAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var result = new HashSet<int>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_migrations ORDER BY version;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetInt32(0));
        }
        return result;
    }

    private static async Task ApplyMigrationAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        SqliteMigration migration,
        CancellationToken cancellationToken)
    {
        await using var transaction = connection.BeginTransaction();
        try
        {
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = migration.Sql;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var record = connection.CreateCommand())
            {
                record.Transaction = transaction;
                record.CommandText = """
                    INSERT INTO schema_migrations (version, name, applied_at_utc)
                    VALUES ($version, $name, $appliedAt);
                    """;
                record.Parameters.AddWithValue("$version", migration.Version);
                record.Parameters.AddWithValue("$name", migration.Name);
                record.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
                await record.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static void ValidateMigrationList()
    {
        if (Migrations.Count == 0 || Migrations[^1].Version != CurrentSchemaVersion)
            throw new InvalidOperationException("SQLite migration list does not match CurrentSchemaVersion.");
        if (Migrations.Select(x => x.Version).Distinct().Count() != Migrations.Count)
            throw new InvalidOperationException("SQLite migration versions must be unique.");
        if (Migrations.Any(x => x.Version <= 0))
            throw new InvalidOperationException("SQLite migration versions must be positive integers.");
    }
}
