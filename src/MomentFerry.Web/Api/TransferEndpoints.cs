using MomentFerry.Application.Abstractions;
using MomentFerry.Application.Services;
using MomentFerry.Web.Diagnostics;
using System.Text;

namespace MomentFerry.Web.Api;

public static class TransferEndpoints
{
    public static IEndpointRouteBuilder MapTransferEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/operations", async (
            int? limit,
            IMediaOperationRepository repository,
            CancellationToken ct) =>
            Results.Ok(await repository.ListRecentAsync(Math.Clamp(limit ?? 200, 1, 2000), ct)));

        app.MapGet("/api/v1/operations/export.csv", async (
            int? limit,
            IMediaOperationRepository repository,
            CancellationToken ct) =>
        {
            var csv = AuditExportService.ToCsv(await repository.ListRecentAsync(Math.Clamp(limit ?? 5000, 1, 5000), ct));
            return Results.File(
                Encoding.UTF8.GetBytes(csv),
                "text/csv; charset=utf-8",
                $"momentferry-audit-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.csv");
        });

        // Both states block their media file from being routed again and are only resolved by a user
        // decision, so they are reported together as the "needs your decision" list.
        app.MapGet("/api/v1/logs", (
            int? limit,
            string? level,
            ActivityLog log) =>
        {
            if (!Enum.TryParse<LogLevel>(level ?? nameof(LogLevel.Information), true, out var minimumLevel))
                return Results.BadRequest(new { error = "Unknown log level." });

            return Results.Ok(log.Recent(Math.Clamp(limit ?? 200, 1, 500), minimumLevel));
        });

        app.MapGet("/api/v1/quarantine", async (
            int? limit,
            IMediaOperationRepository repository,
            CancellationToken ct) =>
        {
            var max = Math.Clamp(limit ?? 200, 1, 2000);
            var quarantined = await repository.ListByStateAsync(
                MomentFerry.Core.Domain.MediaOperationState.Quarantined,
                max,
                ct);
            var retryPending = await repository.ListByStateAsync(
                MomentFerry.Core.Domain.MediaOperationState.RetryPending,
                max,
                ct);
            return Results.Ok(quarantined
                .Concat(retryPending)
                .OrderByDescending(x => x.StartedAt)
                .Take(max)
                .ToArray());
        });

        app.MapPost("/api/v1/quarantine/{id:guid}/dismiss", async (
            Guid id,
            QuarantineDismissRequest request,
            QuarantineService quarantine,
            CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await quarantine.DismissAsync(id, request.ResolutionNote, ct));
            }
            catch (FileNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/v1/transfers", async (
            TransferRequest request,
            IRuntimeSettingsStore settingsStore,
            TransferCoordinator transfer,
            CancellationToken ct) =>
        {
            if ((await settingsStore.GetAsync(ct)).DryRun) return DryRunConflict();

            try
            {
                return Results.Ok(await transfer.ExecuteOnceAsync(request.MediaFileId, request.EventId, ct));
            }
            catch (FileNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or NotSupportedException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/v1/operations/{id:guid}/retry", async (
            Guid id,
            IRuntimeSettingsStore settingsStore,
            IMediaOperationRepository operations,
            IMediaEventRepository events,
            IShareRepository shares,
            IFileSystemGateway fileSystem,
            SafeTransferService transfer,
            IClock clock,
            CancellationToken ct) =>
        {
            if ((await settingsStore.GetAsync(ct)).DryRun) return DryRunConflict();

            try
            {
                var retry = new OperationRetryService(operations, events, shares, fileSystem, transfer, clock);
                return Results.Ok(await retry.RetryAsync(id, ct));
            }
            catch (FileNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or NotSupportedException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/v1/operations/{id:guid}/route-again", async (
            Guid id,
            IRuntimeSettingsStore settingsStore,
            IMediaOperationRepository operations,
            IMediaEventRepository events,
            IShareRepository shares,
            IFileSystemGateway fileSystem,
            SafeTransferService transfer,
            IClock clock,
            CancellationToken ct) =>
        {
            if ((await settingsStore.GetAsync(ct)).DryRun) return DryRunConflict();

            try
            {
                var retry = new OperationRetryService(operations, events, shares, fileSystem, transfer, clock);
                return Results.Ok(await retry.RouteAgainAsync(id, ct));
            }
            catch (FileNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or NotSupportedException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/v1/recovery", async (
            OperationRecoveryService recovery,
            CancellationToken ct) => Results.Ok(await recovery.RecoverAsync(ct)));

        return app;
    }

    private static IResult DryRunConflict() => Results.Conflict(new
    {
        error = "MomentFerry is in Dry Run mode. Disable Dry Run in Settings before executing or retrying transfers."
    });
}

public sealed record TransferRequest(Guid MediaFileId, Guid EventId);
public sealed record QuarantineDismissRequest(string? ResolutionNote);
