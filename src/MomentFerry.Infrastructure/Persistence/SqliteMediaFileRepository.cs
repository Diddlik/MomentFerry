using MomentFerry.Application.Abstractions;
using MomentFerry.Core.Domain;

namespace MomentFerry.Infrastructure.Persistence;

public sealed class SqliteMediaFileRepository(SqliteConnectionFactory connectionFactory) : IMediaFileRepository
{
    /// <summary>Sentinel last-seen value that sorts requeued files alongside never-indexed ones.</summary>
    private static readonly string RequeuedMarker = DateTimeOffset.MinValue.UtcDateTime.ToString("O");

    private const string SelectColumns = "id, source_share_id, source_path, original_name, size, extension, media_type, captured_at_utc, timestamp_source, timezone_inferred, sha256, source_last_write_at_utc, first_seen_at_utc, last_seen_at_utc, camera_make, camera_model, captured_at_offset_minutes";

    public async Task<IReadOnlyList<MediaFile>> ListRecentAsync(int limit = 200, CancellationToken cancellationToken = default)
    {
        var result = new List<MediaFile>();
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM media_files ORDER BY last_seen_at_utc DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(Read(reader));
        }
        return result;
    }

    public async Task<IReadOnlyList<MediaFile>> ListBySourceAsync(
        Guid sourceShareId,
        CancellationToken cancellationToken = default)
    {
        var result = new List<MediaFile>();
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM media_files WHERE source_share_id = $shareId";
        command.Parameters.AddWithValue("$shareId", sourceShareId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(Read(reader));
        }
        return result;
    }

    public async Task<MediaFile?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM media_files WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<MediaFile?> GetBySourceAsync(
        Guid sourceShareId,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM media_files WHERE source_share_id = $shareId AND source_path = $path";
        command.Parameters.AddWithValue("$shareId", sourceShareId.ToString("D"));
        command.Parameters.AddWithValue("$path", sourcePath);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<int> ClearMetadataStampAsync(Guid? shareId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = shareId is null
            ? "UPDATE media_files SET source_last_write_at_utc = NULL;"
            : "UPDATE media_files SET source_last_write_at_utc = NULL WHERE source_share_id = $shareId;";
        if (shareId is not null) command.Parameters.AddWithValue("$shareId", shareId.Value.ToString("D"));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> DeleteUnreferencedAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0) return 0;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM media_files
            WHERE id = $id
              AND NOT EXISTS (SELECT 1 FROM operations WHERE media_file_id = media_files.id);
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$id";
        command.Parameters.Add(parameter);

        var removed = 0;
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            parameter.Value = id.ToString("D");
            removed += await command.ExecuteNonQueryAsync(cancellationToken);
        }

        transaction.Commit();
        return removed;
    }

    public async Task UpsertAsync(MediaFile mediaFile, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO media_files (
                id, source_share_id, source_path, original_name, size, extension, media_type,
                captured_at_utc, timestamp_source, timezone_inferred, sha256,
                source_last_write_at_utc, first_seen_at_utc, last_seen_at_utc,
                camera_make, camera_model, captured_at_offset_minutes)
            VALUES (
                $id, $sourceShareId, $sourcePath, $originalName, $size, $extension, $mediaType,
                $capturedAt, $timestampSource, $timezoneInferred, $sha256,
                $sourceLastWrite, $firstSeen, $lastSeen, $cameraMake, $cameraModel,
                $capturedAtOffset)
            ON CONFLICT(source_share_id, source_path) DO UPDATE SET
                original_name = excluded.original_name,
                size = excluded.size,
                extension = excluded.extension,
                media_type = excluded.media_type,
                captured_at_utc = excluded.captured_at_utc,
                timestamp_source = excluded.timestamp_source,
                timezone_inferred = excluded.timezone_inferred,
                sha256 = COALESCE(excluded.sha256, media_files.sha256),
                source_last_write_at_utc = excluded.source_last_write_at_utc,
                last_seen_at_utc = excluded.last_seen_at_utc,
                camera_make = COALESCE(excluded.camera_make, media_files.camera_make),
                camera_model = COALESCE(excluded.camera_model, media_files.camera_model),
                captured_at_offset_minutes = excluded.captured_at_offset_minutes;
            """;
        command.Parameters.AddWithValue("$id", mediaFile.Id.ToString("D"));
        command.Parameters.AddWithValue("$sourceShareId", mediaFile.SourceShareId.ToString("D"));
        command.Parameters.AddWithValue("$sourcePath", mediaFile.SourcePath);
        command.Parameters.AddWithValue("$originalName", mediaFile.OriginalName);
        command.Parameters.AddWithValue("$size", mediaFile.Size);
        command.Parameters.AddWithValue("$extension", mediaFile.Extension);
        command.Parameters.AddWithValue("$mediaType", (int)mediaFile.MediaType);
        command.Parameters.AddWithValue("$capturedAt", mediaFile.CapturedAt is null ? DBNull.Value : mediaFile.CapturedAt.Value.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$timestampSource", (object?)mediaFile.TimestampSource ?? DBNull.Value);
        command.Parameters.AddWithValue("$timezoneInferred", mediaFile.IsTimezoneInferred ? 1 : 0);
        command.Parameters.AddWithValue("$sha256", (object?)mediaFile.Sha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$sourceLastWrite", mediaFile.SourceLastWriteAt is null
            ? DBNull.Value
            : mediaFile.SourceLastWriteAt.Value.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$firstSeen", mediaFile.FirstSeenAt.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$lastSeen", mediaFile.LastSeenAt.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$cameraMake", (object?)mediaFile.CameraMake ?? DBNull.Value);
        command.Parameters.AddWithValue("$cameraModel", (object?)mediaFile.CameraModel ?? DBNull.Value);
        command.Parameters.AddWithValue("$capturedAtOffset", mediaFile.CapturedAtOffsetMinutes is null ? DBNull.Value : mediaFile.CapturedAtOffsetMinutes.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> RequeueByCaptureWindowAsync(
        IReadOnlyCollection<Guid> sourceShareIds,
        DateTimeOffset startAt,
        DateTimeOffset? endAt,
        CancellationToken cancellationToken = default)
    {
        if (sourceShareIds.Count == 0) return 0;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        var shareParameters = new List<string>(sourceShareIds.Count);
        var shareIndex = 0;
        foreach (var shareId in sourceShareIds)
        {
            var name = $"$share{shareIndex++}";
            shareParameters.Add(name);
            command.Parameters.AddWithValue(name, shareId.ToString("D"));
        }

        // Capture windows are inclusive on both ends, mirroring IMediaEventRepository.ListMatchableAsync.
        command.CommandText = $"""
            UPDATE media_files
            SET last_seen_at_utc = $requeued
            WHERE source_share_id IN ({string.Join(", ", shareParameters)})
              AND captured_at_utc IS NOT NULL
              AND captured_at_utc >= $start
              AND ($end IS NULL OR captured_at_utc <= $end)
              AND last_seen_at_utc > $requeued;
            """;
        command.Parameters.AddWithValue("$requeued", RequeuedMarker);
        command.Parameters.AddWithValue("$start", startAt.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$end", endAt is null ? DBNull.Value : endAt.Value.UtcDateTime.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static MediaFile Read(Microsoft.Data.Sqlite.SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        SourceShareId = Guid.Parse(reader.GetString(1)),
        SourcePath = reader.GetString(2),
        OriginalName = reader.GetString(3),
        Size = reader.GetInt64(4),
        Extension = reader.GetString(5),
        MediaType = (MediaType)reader.GetInt32(6),
        CapturedAt = reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7)),
        TimestampSource = reader.IsDBNull(8) ? null : reader.GetString(8),
        IsTimezoneInferred = reader.GetInt32(9) != 0,
        Sha256 = reader.IsDBNull(10) ? null : reader.GetString(10),
        SourceLastWriteAt = reader.IsDBNull(11) ? null : DateTimeOffset.Parse(reader.GetString(11)),
        FirstSeenAt = DateTimeOffset.Parse(reader.GetString(12)),
        LastSeenAt = DateTimeOffset.Parse(reader.GetString(13)),
        CameraMake = reader.IsDBNull(14) ? null : reader.GetString(14),
        CameraModel = reader.IsDBNull(15) ? null : reader.GetString(15),
        CapturedAtOffsetMinutes = reader.IsDBNull(16) ? null : reader.GetInt32(16)
    };
}
