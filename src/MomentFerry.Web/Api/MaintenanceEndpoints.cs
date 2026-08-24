using MomentFerry.Application.Abstractions;
using MomentFerry.Core.Domain;
using MomentFerry.Infrastructure.Persistence;
using MomentFerry.Web.Background;

namespace MomentFerry.Web.Api;

/// <summary>
/// Manual housekeeping. Every action here is destructive in some measure, so none of them runs on a
/// schedule and each one reports exactly how much it touched.
/// </summary>
public static class MaintenanceEndpoints
{
    public static IEndpointRouteBuilder MapMaintenanceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/maintenance");

        group.MapGet("/", async (
            IMediaFileRepository mediaFiles,
            IMediaOperationRepository operations,
            SqliteConnectionFactory connectionFactory,
            CancellationToken ct) =>
        {
            var counts = await operations.CountByStateAsync(ct);
            return Results.Ok(new
            {
                databaseBytes = DatabaseBytes(connectionFactory),
                operations = counts.ToDictionary(x => x.Key.ToString(), x => x.Value),
                indexedMediaFiles = (await mediaFiles.ListRecentAsync(int.MaxValue, ct)).Count
            });
        });

        // Clearing the last-write stamp is what makes the next cycle extract metadata again: routing
        // reuses the index only while size and last-write time still match what it recorded.
        group.MapPost("/reindex-metadata", async (
            Guid? shareId,
            IMediaFileRepository mediaFiles,
            AutomationWakeSignal wakeSignal,
            CancellationToken ct) =>
        {
            var affected = await mediaFiles.ClearMetadataStampAsync(shareId, ct);
            wakeSignal.Wake();
            return Results.Ok(new { affected });
        });

        group.MapPost("/forget-missing", async (
            IMediaFileRepository mediaFiles,
            IFileSystemGateway fileSystem,
            CancellationToken ct) =>
        {
            var indexed = await mediaFiles.ListRecentAsync(int.MaxValue, ct);
            var missing = indexed
                .Where(x => !fileSystem.FileExists(x.SourcePath))
                .Select(x => x.Id)
                .ToArray();

            var removed = await mediaFiles.DeleteUnreferencedAsync(missing, ct);
            return Results.Ok(new
            {
                missing = missing.Length,
                removed,
                // The rest carry an operation. Deleting those rows would cascade into the history that
                // records a file was verified before its source was released.
                keptForHistory = missing.Length - removed
            });
        });

        group.MapPost("/prune-operations", async (
            int? olderThanDays,
            IRuntimeSettingsStore settingsStore,
            IMediaOperationRepository operations,
            IClock clock,
            CancellationToken ct) =>
        {
            var days = olderThanDays ?? (await settingsStore.GetAsync(ct)).OperationRetentionDays;
            if (days <= 0)
                return Results.BadRequest(new { error = "Set a retention window of at least one day." });

            var removed = await operations.DeleteFinishedBeforeAsync(clock.UtcNow.AddDays(-days), ct);
            return Results.Ok(new { removed, olderThanDays = days });
        });

        group.MapPost("/compact", (SqliteConnectionFactory connectionFactory) =>
        {
            var before = DatabaseBytes(connectionFactory);
            using var connection = connectionFactory.OpenAsync().GetAwaiter().GetResult();
            using var command = connection.CreateCommand();
            command.CommandText = "VACUUM;";
            command.ExecuteNonQuery();
            var after = DatabaseBytes(connectionFactory);
            return Results.Ok(new { before, after, reclaimed = before is null || after is null ? null : before - after });
        });

        return app;
    }

    private static long? DatabaseBytes(SqliteConnectionFactory connectionFactory)
    {
        try
        {
            var file = new FileInfo(connectionFactory.DatabasePath);
            return file.Exists ? file.Length : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
