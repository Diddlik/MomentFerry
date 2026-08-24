using MomentFerry.Application.Abstractions;
using MomentFerry.Core.Domain;

namespace MomentFerry.Infrastructure.Persistence;

public sealed class SqliteMediaOperationRepository(SqliteConnectionFactory connectionFactory) : IMediaOperationRepository
{
    private const string SelectColumns = "id, media_file_id, event_id, state, source_path, staging_path, destination_path, source_hash, destination_hash, retry_count, last_error, started_at_utc, completed_at_utc";

    public async Task<IReadOnlyList<MediaOperation>> ListRecentAsync(int limit = 200, CancellationToken cancellationToken = default)
    {
        var result = new List<MediaOperation>();
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM operations ORDER BY updated_at_utc DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(Read(reader));
        return result;
    }

    public async Task<IReadOnlyList<MediaOperation>> ListByStateAsync(
        MediaOperationState state,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        var result = new List<MediaOperation>();
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM operations WHERE state = $state ORDER BY updated_at_utc DESC LIMIT $limit";
        command.Parameters.AddWithValue("$state", (int)state);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(Read(reader));
        return result;
    }

    public async Task<IReadOnlyDictionary<MediaOperationState, long>> CountByStateAsync(
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<MediaOperationState, long>();
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT state, COUNT(*) FROM operations GROUP BY state";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result[(MediaOperationState)reader.GetInt32(0)] = reader.GetInt64(1);
        return result;
    }

    public async Task<IReadOnlyList<MediaOperation>> ListIncompleteAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<MediaOperation>();
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns} FROM operations
            WHERE state NOT IN ($completed, $ignored, $failed, $quarantined)
            ORDER BY updated_at_utc;
            """;
        command.Parameters.AddWithValue("$completed", (int)MediaOperationState.Completed);
        command.Parameters.AddWithValue("$ignored", (int)MediaOperationState.Ignored);
        command.Parameters.AddWithValue("$failed", (int)MediaOperationState.Failed);
        command.Parameters.AddWithValue("$quarantined", (int)MediaOperationState.Quarantined);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(Read(reader));
        return result;
    }

    public async Task<MediaOperation?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM operations WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<MediaOperation?> GetIncompleteByMediaFileAsync(Guid mediaFileId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns} FROM operations
            WHERE media_file_id = $mediaFileId
              AND state NOT IN ($completed, $ignored, $failed)
            ORDER BY updated_at_utc DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$mediaFileId", mediaFileId.ToString("D"));
        command.Parameters.AddWithValue("$completed", (int)MediaOperationState.Completed);
        command.Parameters.AddWithValue("$ignored", (int)MediaOperationState.Ignored);
        command.Parameters.AddWithValue("$failed", (int)MediaOperationState.Failed);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<MediaOperation?> FindCompletedByDestinationHashAsync(
        string destinationHash,
        Guid excludedMediaFileId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(destinationHash)) return null;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectColumns} FROM operations
            WHERE destination_hash = $destinationHash COLLATE NOCASE
              AND media_file_id <> $excludedMediaFileId
              AND state = $completed
            ORDER BY updated_at_utc LIMIT 1;
            """;
        command.Parameters.AddWithValue("$destinationHash", destinationHash);
        command.Parameters.AddWithValue("$excludedMediaFileId", excludedMediaFileId.ToString("D"));
        command.Parameters.AddWithValue("$completed", (int)MediaOperationState.Completed);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<bool> HasTerminalOperationAsync(
Guid mediaFileId, Guid eventId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1 FROM operations
            WHERE media_file_id = $mediaFileId
              AND event_id = $eventId
              AND state IN ($completed, $ignored)
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$mediaFileId", mediaFileId.ToString("D"));
        command.Parameters.AddWithValue("$eventId", eventId.ToString("D"));
        command.Parameters.AddWithValue("$completed", (int)MediaOperationState.Completed);
        command.Parameters.AddWithValue("$ignored", (int)MediaOperationState.Ignored);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task<int> SupersedeTerminalByEventAsync(
        Guid eventId,
        string reason,
        DateTimeOffset supersededAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE operations
            SET state = $failed,
                last_error = $reason,
                completed_at_utc = $supersededAt,
                updated_at_utc = $now
            WHERE event_id = $eventId
              AND state IN ($completed, $ignored);
            """;
        command.Parameters.AddWithValue("$failed", (int)MediaOperationState.Failed);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$supersededAt", supersededAt.ToString("O"));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$eventId", eventId.ToString("D"));
        command.Parameters.AddWithValue("$completed", (int)MediaOperationState.Completed);
        command.Parameters.AddWithValue("$ignored", (int)MediaOperationState.Ignored);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertAsync(MediaOperation operation, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO operations (
                id, media_file_id, event_id, state, source_path, staging_path, destination_path,
                source_hash, destination_hash, retry_count, last_error,
                started_at_utc, completed_at_utc, updated_at_utc)
            VALUES (
                $id, $mediaFileId, $eventId, $state, $sourcePath, $stagingPath, $destinationPath,
                $sourceHash, $destinationHash, $retryCount, $lastError,
                $startedAt, $completedAt, $updatedAt)
            ON CONFLICT(id) DO UPDATE SET
                event_id = excluded.event_id,
                state = excluded.state,
                staging_path = excluded.staging_path,
                destination_path = excluded.destination_path,
                source_hash = excluded.source_hash,
                destination_hash = excluded.destination_hash,
                retry_count = excluded.retry_count,
                last_error = excluded.last_error,
                completed_at_utc = excluded.completed_at_utc,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$id", operation.Id.ToString("D"));
        command.Parameters.AddWithValue("$mediaFileId", operation.MediaFileId.ToString("D"));
        command.Parameters.AddWithValue("$eventId", operation.EventId is null ? DBNull.Value : operation.EventId.Value.ToString("D"));
        command.Parameters.AddWithValue("$state", (int)operation.State);
        command.Parameters.AddWithValue("$sourcePath", operation.SourcePath);
        command.Parameters.AddWithValue("$stagingPath", (object?)operation.StagingPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$destinationPath", (object?)operation.DestinationPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$sourceHash", (object?)operation.SourceHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$destinationHash", (object?)operation.DestinationHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$retryCount", operation.RetryCount);
        command.Parameters.AddWithValue("$lastError", (object?)operation.LastError ?? DBNull.Value);
        command.Parameters.AddWithValue("$startedAt", operation.StartedAt.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$completedAt", operation.CompletedAt is null ? DBNull.Value : operation.CompletedAt.Value.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static MediaOperation Read(Microsoft.Data.Sqlite.SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        MediaFileId = Guid.Parse(reader.GetString(1)),
        EventId = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
        State = (MediaOperationState)reader.GetInt32(3),
        SourcePath = reader.GetString(4),
        StagingPath = reader.IsDBNull(5) ? null : reader.GetString(5),
        DestinationPath = reader.IsDBNull(6) ? null : reader.GetString(6),
        SourceHash = reader.IsDBNull(7) ? null : reader.GetString(7),
        DestinationHash = reader.IsDBNull(8) ? null : reader.GetString(8),
        RetryCount = reader.GetInt32(9),
        LastError = reader.IsDBNull(10) ? null : reader.GetString(10),
        StartedAt = DateTimeOffset.Parse(reader.GetString(11)),
        CompletedAt = reader.IsDBNull(12) ? null : DateTimeOffset.Parse(reader.GetString(12))
    };
}
