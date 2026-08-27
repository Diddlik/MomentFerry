using MomentFerry.Application.Abstractions;
using MomentFerry.Core.Domain;
using MomentFerry.Infrastructure;
using MomentFerry.Web.Background;
using MomentFerry.Web.Security;
using MomentFerry.Web.Updates;
using System.Globalization;
using System.Text;

namespace MomentFerry.Web.Api;

public static class SettingsEndpoints
{
    private const string LiveModeConfirmation = "ENABLE_LIVE_TRANSFERS";

    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/settings", async (
            IRuntimeSettingsStore store,
            CancellationToken ct) =>
            Results.Ok(await store.GetAsync(ct)));

        app.MapPut("/api/v1/settings", async (
            RuntimeSettingsRequest request,
            IRuntimeSettingsStore store,
            PasswordProtectionOptions passwordProtection,
            ImageUpdateWakeSignal updateWakeSignal,
            CancellationToken ct) =>
        {
            if (request.ReconciliationIntervalSeconds is < 15 or > 86400)
                return Results.BadRequest(new { error = "ReconciliationIntervalSeconds must be between 15 and 86400." });
            if (request.MaxFilesPerSharePerCycle is < 1 or > 2000)
                return Results.BadRequest(new { error = "MaxFilesPerSharePerCycle must be between 1 and 2000." });
            if (request.MaxParallelMetadataReads is < 1 or > 8)
                return Results.BadRequest(new { error = "MaxParallelMetadataReads must be between 1 and 8." });
            if (request.MinimumFreeSpaceReserveBytes is < 0 or > 1099511627776)
                return Results.BadRequest(new { error = "MinimumFreeSpaceReserveBytes must be between 0 and 1099511627776." });
            if (request.OperationRetentionDays is < 0 or > 3650)
                return Results.BadRequest(new { error = "OperationRetentionDays must be between 0 and 3650." });
            if (request.PasswordProtectionEnabled == true && !passwordProtection.IsConfigured)
                return Results.BadRequest(new { error = "Configure a username and a password of at least 12 characters before enabling access protection." });

            var current = await store.GetAsync(ct);
            if (current.DryRun && !request.DryRun &&
                !string.Equals(request.LiveModeConfirmation, LiveModeConfirmation, StringComparison.Ordinal))
            {
                return Results.Conflict(new
                {
                    error = $"Switching from Dry Run to Live requires confirmation token '{LiveModeConfirmation}'."
                });
            }

            var updated = await store.UpdateAsync(new MomentFerryRuntimeSettings(
                request.DryRun,
                request.AutomationEnabled,
                request.ReconciliationIntervalSeconds,
                request.MaxFilesPerSharePerCycle,
                request.MaxParallelMetadataReads,
                request.AllowFilesystemTimestampFallback,
                request.MinimumFreeSpaceReserveBytes ?? current.MinimumFreeSpaceReserveBytes,
                request.AutomaticImageUpdatesEnabled ?? current.AutomaticImageUpdatesEnabled,
                request.OperationRetentionDays ?? current.OperationRetentionDays,
                request.PasswordProtectionEnabled ?? current.PasswordProtectionEnabled), ct);

            // Turning automatic updates on checks now instead of whenever the six-hour period that was
            // running while it was still off happens to end.
            if (updated.AutomaticImageUpdatesEnabled && !current.AutomaticImageUpdatesEnabled)
                updateWakeSignal.Wake();
            if (current.PasswordProtectionEnabled && !updated.PasswordProtectionEnabled)
                passwordProtection.RevokeAllSessions();

            return Results.Ok(updated);
        });

        app.MapDelete("/api/v1/settings", async (
            IRuntimeSettingsStore store,
            CancellationToken ct) => Results.Ok(await store.ResetAsync(ct)));

        app.MapGet("/api/v1/status", async (
            IRuntimeSettingsStore store,
            AutomationStatus automationStatus,
            CancellationToken ct) =>
        {
            var settings = await store.GetAsync(ct);
            return Results.Ok(new
            {
                mode = settings.DryRun ? "dry-run" : "live",
                settings.AutomationEnabled,
                settings.ReconciliationIntervalSeconds,
                settings.MaxFilesPerSharePerCycle,
                settings.MaxParallelMetadataReads,
                settings.AllowFilesystemTimestampFallback,
                settings.MinimumFreeSpaceReserveBytes,
                settings.AutomaticImageUpdatesEnabled,
                settings.OperationRetentionDays,
                settings.PasswordProtectionEnabled,
                automation = automationStatus.Snapshot()
            });
        });

        app.MapPost("/api/v1/automation/run", async (
            IRuntimeSettingsStore store,
            AutomationStatus automationStatus,
            AutomationWakeSignal wakeSignal,
            IClock clock,
            CancellationToken ct) =>
        {
            var settings = await store.GetAsync(ct);
            if (!settings.AutomationEnabled)
                return Results.Conflict(new { error = "Automation is disabled." });
            if (automationStatus.Snapshot().CycleRunning)
                return Results.Conflict(new { error = "An automation cycle is already running." });

            var requestedAt = clock.UtcNow;
            wakeSignal.Wake();
            return Results.Accepted(value: new { requestedAt });
        });

        app.MapGet("/api/v1/storage", async (
            IShareRepository shares,
            IFileSystemGateway fileSystem,
            IRuntimeSettingsStore store,
            CancellationToken ct) =>
        {
            var settings = await store.GetAsync(ct);
            var destinations = (await shares.ListAsync(ct))
                .Where(x => x.Enabled && x.Role is ShareRole.Destination or ShareRole.Both)
                .ToArray();
            var items = new List<StorageShareStatus>(destinations.Length);

            foreach (var share in destinations)
            {
                long? freeBytes = null;
                string? error = null;
                try
                {
                    freeBytes = fileSystem.GetAvailableFreeSpace(share.Path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    error = ex.Message;
                }

                items.Add(new StorageShareStatus(
                    share.Id,
                    share.Name,
                    share.Path,
                    fileSystem.DirectoryExists(share.Path),
                    freeBytes,
                    freeBytes is long value && value < settings.MinimumFreeSpaceReserveBytes,
                    error));
            }

            return Results.Ok(new
            {
                settings.MinimumFreeSpaceReserveBytes,
                items
            });
        });

        app.MapGet("/metrics", async (
            IRuntimeSettingsStore store,
            IMediaOperationRepository operations,
            AutomationStatus automationStatus,
            CancellationToken ct) =>
        {
            var settings = await store.GetAsync(ct);
            var counts = await operations.CountByStateAsync(ct);
            var automation = automationStatus.Snapshot();
            var metrics = new StringBuilder()
                .AppendLine("# HELP momentferry_automation_enabled Whether automatic reconciliation is enabled.")
                .AppendLine("# TYPE momentferry_automation_enabled gauge")
                .Append("momentferry_automation_enabled ").AppendLine(settings.AutomationEnabled ? "1" : "0")
                .AppendLine("# HELP momentferry_dry_run Whether destructive transfers are disabled.")
                .AppendLine("# TYPE momentferry_dry_run gauge")
                .Append("momentferry_dry_run ").AppendLine(settings.DryRun ? "1" : "0")
                .AppendLine("# HELP momentferry_operations_total Persisted operations by current state.")
                .AppendLine("# TYPE momentferry_operations_total gauge");
            foreach (var state in Enum.GetValues<MomentFerry.Core.Domain.MediaOperationState>())
                metrics.Append("momentferry_operations_total{state=\"")
                    .Append(state.ToString().ToLowerInvariant())
                    .Append("\"} ")
                    .AppendLine(counts.GetValueOrDefault(state).ToString(CultureInfo.InvariantCulture));
            metrics.AppendLine("# HELP momentferry_last_cycle_errors Errors in the latest reconciliation cycle.")
                .AppendLine("# TYPE momentferry_last_cycle_errors gauge")
                .Append("momentferry_last_cycle_errors ")
                .AppendLine(automation.LastErrors.ToString(CultureInfo.InvariantCulture));
            return Results.Text(metrics.ToString(), "text/plain; version=0.0.4; charset=utf-8");
        });

        return app;
    }
}

public sealed record RuntimeSettingsRequest(
    bool DryRun,
    bool AutomationEnabled,
    int ReconciliationIntervalSeconds,
    int MaxFilesPerSharePerCycle,
    int MaxParallelMetadataReads,
    bool AllowFilesystemTimestampFallback,
    long? MinimumFreeSpaceReserveBytes = null,
    bool? AutomaticImageUpdatesEnabled = null,
    int? OperationRetentionDays = null,
    bool? PasswordProtectionEnabled = null,
    string? LiveModeConfirmation = null);

public sealed record StorageShareStatus(
    Guid ShareId,
    string Name,
    string Path,
    bool Exists,
    long? AvailableFreeSpaceBytes,
    bool BelowReserve,
    string? Error);
