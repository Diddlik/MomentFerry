using Microsoft.Data.Sqlite;
using MomentFerry.Application.Abstractions;
using MomentFerry.Core.Domain;

namespace MomentFerry.Infrastructure.Persistence;

public sealed class SqliteRenamePresetRepository(SqliteConnectionFactory connectionFactory) : IRenamePresetRepository
{
    public async Task<IReadOnlyList<RenamePreset>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<RenamePreset>();
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, template FROM rename_presets ORDER BY name";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(Read(reader));
        }
        return result;
    }

    public async Task<RenamePreset?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, template FROM rename_presets WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task UpsertAsync(RenamePreset preset, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO rename_presets (id, name, template, created_at_utc, updated_at_utc)
            VALUES ($id, $name, $template, $created, $updated)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                template = excluded.template,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$id", preset.Id.ToString("D"));
        command.Parameters.AddWithValue("$name", preset.Name.Trim());
        command.Parameters.AddWithValue("$template", preset.Template.Trim());
        command.Parameters.AddWithValue("$created", now);
        command.Parameters.AddWithValue("$updated", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // Shares referencing this preset fall back to no renaming rather than to a dangling id.
        command.CommandText = """
            UPDATE shares SET rename_preset_id = NULL WHERE rename_preset_id = $id;
            DELETE FROM rename_presets WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static RenamePreset Read(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        Name = reader.GetString(1),
        Template = reader.GetString(2)
    };
}

public sealed class SqliteCameraMappingRepository(SqliteConnectionFactory connectionFactory) : ICameraMappingRepository
{
    public async Task<IReadOnlyList<CameraMapping>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<CameraMapping>();
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, from_value, to_value FROM camera_mappings ORDER BY from_value";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new CameraMapping
            {
                Id = Guid.Parse(reader.GetString(0)),
                From = reader.GetString(1),
                To = reader.GetString(2)
            });
        }
        return result;
    }

    public async Task UpsertAsync(CameraMapping mapping, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO camera_mappings (id, from_value, to_value, created_at_utc, updated_at_utc)
            VALUES ($id, $from, $to, $created, $updated)
            ON CONFLICT(id) DO UPDATE SET
                from_value = excluded.from_value,
                to_value = excluded.to_value,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$id", mapping.Id.ToString("D"));
        command.Parameters.AddWithValue("$from", mapping.From.Trim());
        command.Parameters.AddWithValue("$to", mapping.To.Trim());
        command.Parameters.AddWithValue("$created", now);
        command.Parameters.AddWithValue("$updated", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM camera_mappings WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }
}
