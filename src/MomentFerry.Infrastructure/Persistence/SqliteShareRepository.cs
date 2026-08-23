using System.Text.Json;
using MomentFerry.Application.Abstractions;
using MomentFerry.Core.Domain;

namespace MomentFerry.Infrastructure.Persistence;

public sealed class SqliteShareRepository(SqliteConnectionFactory connectionFactory) : IShareRepository
{
    public async Task<IReadOnlyList<Share>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<Share>();
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, path, role, enabled, owner, group_name, preset, stability_seconds, recursive, default_timezone, ignore_patterns_json, allowed_media_types_json, image_extensions_json, video_extensions_json, image_subfolder, video_subfolder, rename_preset_id FROM shares ORDER BY name";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(Read(reader));
        }
        return result;
    }

    public async Task<Share?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, path, role, enabled, owner, group_name, preset, stability_seconds, recursive, default_timezone, ignore_patterns_json, allowed_media_types_json, image_extensions_json, video_extensions_json, image_subfolder, video_subfolder, rename_preset_id FROM shares WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task UpsertAsync(Share share, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO shares (
                id, name, path, role, enabled, owner, group_name, preset,
                stability_seconds, recursive, default_timezone,
                ignore_patterns_json, allowed_media_types_json,
                image_extensions_json, video_extensions_json, image_subfolder, video_subfolder,
                rename_preset_id, created_at_utc, updated_at_utc)
            VALUES (
                $id, $name, $path, $role, $enabled, $owner, $group, $preset,
                $stability, $recursive, $timezone, $ignore, $types,
                $imageExtensions, $videoExtensions, $imageSubfolder, $videoSubfolder,
                $renamePreset, $created, $updated)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                path = excluded.path,
                role = excluded.role,
                enabled = excluded.enabled,
                owner = excluded.owner,
                group_name = excluded.group_name,
                preset = excluded.preset,
                stability_seconds = excluded.stability_seconds,
                recursive = excluded.recursive,
                default_timezone = excluded.default_timezone,
                ignore_patterns_json = excluded.ignore_patterns_json,
                allowed_media_types_json = excluded.allowed_media_types_json,
                image_extensions_json = excluded.image_extensions_json,
                video_extensions_json = excluded.video_extensions_json,
                image_subfolder = excluded.image_subfolder,
                video_subfolder = excluded.video_subfolder,
                rename_preset_id = excluded.rename_preset_id,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$id", share.Id.ToString("D"));
        command.Parameters.AddWithValue("$name", share.Name.Trim());
        command.Parameters.AddWithValue("$path", share.Path.Trim());
        command.Parameters.AddWithValue("$role", (int)share.Role);
        command.Parameters.AddWithValue("$enabled", share.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$owner", (object?)share.Owner ?? DBNull.Value);
        command.Parameters.AddWithValue("$group", (object?)share.Group ?? DBNull.Value);
        command.Parameters.AddWithValue("$preset", (object?)share.Preset ?? DBNull.Value);
        command.Parameters.AddWithValue("$stability", share.StabilitySeconds);
        command.Parameters.AddWithValue("$recursive", share.Recursive ? 1 : 0);
        command.Parameters.AddWithValue("$timezone", (object?)share.DefaultTimeZone ?? DBNull.Value);
        command.Parameters.AddWithValue("$ignore", JsonSerializer.Serialize(share.IgnorePatterns));
        command.Parameters.AddWithValue("$types", JsonSerializer.Serialize(share.AllowedMediaTypes));
        command.Parameters.AddWithValue("$imageExtensions", JsonSerializer.Serialize(share.ImageExtensions));
        command.Parameters.AddWithValue("$videoExtensions", JsonSerializer.Serialize(share.VideoExtensions));
        command.Parameters.AddWithValue("$imageSubfolder", (object?)share.ImageSubfolder ?? DBNull.Value);
        command.Parameters.AddWithValue("$videoSubfolder", (object?)share.VideoSubfolder ?? DBNull.Value);
        command.Parameters.AddWithValue("$renamePreset", share.RenamePresetId is { } preset ? preset.ToString("D") : DBNull.Value);
        command.Parameters.AddWithValue("$created", now);
        command.Parameters.AddWithValue("$updated", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM shares WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>Rows written before schema 3 have no extension lists and fall back to the built-in ones.</summary>
    private static IReadOnlyList<string> ReadExtensions(Microsoft.Data.Sqlite.SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal)
            ? []
            : MediaExtensionDefaults.Normalize(JsonSerializer.Deserialize<string[]>(reader.GetString(ordinal)));

    private static Share Read(Microsoft.Data.Sqlite.SqliteDataReader reader)
    {
        var ignorePatterns = JsonSerializer.Deserialize<string[]>(reader.GetString(11)) ?? Array.Empty<string>();
        var allowedTypes = JsonSerializer.Deserialize<MediaType[]>(reader.GetString(12)) ?? [MediaType.Image, MediaType.Video];
        return new Share
        {
            Id = Guid.Parse(reader.GetString(0)),
            Name = reader.GetString(1),
            Path = reader.GetString(2),
            Role = (ShareRole)reader.GetInt32(3),
            Enabled = reader.GetInt32(4) != 0,
            Owner = reader.IsDBNull(5) ? null : reader.GetString(5),
            Group = reader.IsDBNull(6) ? null : reader.GetString(6),
            Preset = reader.IsDBNull(7) ? null : reader.GetString(7),
            StabilitySeconds = reader.GetInt32(8),
            Recursive = reader.GetInt32(9) != 0,
            DefaultTimeZone = reader.IsDBNull(10) ? null : reader.GetString(10),
            IgnorePatterns = ignorePatterns,
            AllowedMediaTypes = allowedTypes.ToHashSet(),
            ImageExtensions = ReadExtensions(reader, 13),
            VideoExtensions = ReadExtensions(reader, 14),
            ImageSubfolder = reader.IsDBNull(15) ? null : reader.GetString(15),
            VideoSubfolder = reader.IsDBNull(16) ? null : reader.GetString(16),
            RenamePresetId = reader.IsDBNull(17) ? null : Guid.Parse(reader.GetString(17))
        };
    }
}
