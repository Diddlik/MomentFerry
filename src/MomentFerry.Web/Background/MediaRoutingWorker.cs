using MomentFerry.Application.Abstractions;
using MomentFerry.Application.Services;
using MomentFerry.Core.Domain;

namespace MomentFerry.Web.Background;

public sealed class MediaRoutingWorker(
    IShareRepository shares,
    IMediaEventRepository events,
    ISourceGroupRepository sourceGroups,
    RoutingPreviewService routing,
    ShareDiscoveryService discovery,
    TransferCoordinator transfers,
    IRuntimeSettingsStore runtimeSettings,
    AutomationStatus status,
    AutomationWakeSignal wakeSignal,
    IClock clock,
    IConfiguration configuration,
    ILogger<MediaRoutingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var initialDelay = TimeSpan.FromSeconds(
            Math.Clamp(configuration.GetValue("MomentFerry:Automation:InitialDelaySeconds", 10), 0, 300));
        if (initialDelay > TimeSpan.Zero)
            await Task.Delay(initialDelay, stoppingToken);

        // Tracked independently of the wait loop: watcher traffic resets the wait, and must not be able
        // to postpone the periodic full walk indefinitely.
        var lastFullReconcile = DateTimeOffset.MinValue;
        var pending = new AutomationWakeRequest(true, new Dictionary<Guid, IReadOnlyCollection<string>>(), []);

        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = await runtimeSettings.GetAsync(stoppingToken);
            var interval = TimeSpan.FromSeconds(settings.ReconciliationIntervalSeconds);
            var fullReconcileDue = pending.FullReconcile || clock.UtcNow - lastFullReconcile >= interval;

            // User-initiated backfills run first and to completion: they exist to apply an event the
            // periodic cycle would otherwise take hours to reach.
            foreach (var backfillEventId in pending.BackfillEventIds)
            {
                if (!settings.AutomationEnabled) break;
                status.CycleStarted(clock.UtcNow);
                try
                {
                    var result = await ProcessBackfillAsync(settings, backfillEventId, stoppingToken);
                    status.CycleCompleted(
                        clock.UtcNow,
                        result.SourceShares,
                        result.Matched,
                        result.WouldMove,
                        result.Executed,
                        result.Skipped,
                        result.Errors);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    status.CycleFailed(clock.UtcNow, ex.Message);
                    logger.LogError(ex, "Unhandled error in MomentFerry backfill for event {EventId}", backfillEventId);
                }
            }

            if (settings.AutomationEnabled && (fullReconcileDue || pending.TargetedPaths.Count > 0))
            {
                status.CycleStarted(clock.UtcNow);
                try
                {
                    var result = await ProcessCycleAsync(
                        settings,
                        fullReconcileDue ? null : pending.TargetedPaths,
                        stoppingToken);
                    status.CycleCompleted(
                        clock.UtcNow,
                        result.SourceShares,
                        result.Matched,
                        result.WouldMove,
                        result.Executed,
                        result.Skipped,
                        result.Errors);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    status.CycleFailed(clock.UtcNow, ex.Message);
                    logger.LogError(ex, "Unhandled error in MomentFerry automation cycle");
                }
            }

            // Stamped after the cycle, not before: the interval is a rest gap between walks, so a share
            // slower than the interval still gets a full pause instead of running back to back. This also
            // consumes the tick while automation is disabled, which would otherwise spin on a zero wait.
            if (fullReconcileDue) lastFullReconcile = clock.UtcNow;

            settings = await runtimeSettings.GetAsync(stoppingToken);
            var nextFullReconcile = lastFullReconcile + TimeSpan.FromSeconds(settings.ReconciliationIntervalSeconds);
            var untilFullReconcile = nextFullReconcile - clock.UtcNow;
            if (untilFullReconcile < TimeSpan.Zero) untilFullReconcile = TimeSpan.Zero;

            pending = await wakeSignal.WaitAsync(untilFullReconcile, stoppingToken);
        }
    }

    /// <summary>
    /// Runs one routing cycle. A null <paramref name="targetedPaths"/> performs the periodic full walk;
    /// otherwise only the named watcher paths are evaluated, skipping share enumeration entirely.
    /// </summary>
    private async Task<CycleResult> ProcessCycleAsync(
        MomentFerryRuntimeSettings settings,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<string>>? targetedPaths,
        CancellationToken cancellationToken)
    {
        var matched = 0;
        var wouldMove = 0;
        var executed = 0;
        var skipped = 0;
        var errors = 0;

        var sourceShares = (await shares.ListAsync(cancellationToken))
            .Where(x => x.Enabled && x.Role is ShareRole.Source or ShareRole.Both)
            .Where(x => targetedPaths is null || targetedPaths.ContainsKey(x.Id))
            .ToArray();

        foreach (var sourceShare in sourceShares)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<RoutingPreviewItem> items;
            try
            {
                void Report(RoutingPreviewProgress progress) => status.Progress(
                    sourceShare.Name,
                    progress.Phase,
                    progress.Processed,
                    progress.Total);

                items = targetedPaths is null
                    ? await routing.PreviewAsync(
                        sourceShare,
                        settings.MaxFilesPerSharePerCycle,
                        cancellationToken,
                        settings.MaxParallelMetadataReads,
                        Report)
                    : await routing.EvaluateAsync(
                        sourceShare,
                        ObserveTargeted(sourceShare, targetedPaths[sourceShare.Id]),
                        cancellationToken,
                        settings.MaxParallelMetadataReads,
                        Report);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                errors++;
                logger.LogWarning(ex, "Automation scan failed for share {Share}", sourceShare.Name);
                continue;
            }

            var tally = await RouteAsync(settings, items, null, cancellationToken);
            matched += tally.Matched;
            wouldMove += tally.WouldMove;
            executed += tally.Executed;
            skipped += tally.Skipped;
            errors += tally.Errors;
        }

        return new CycleResult(sourceShares.Length, matched, wouldMove, executed, skipped, errors);
    }

    /// <summary>
    /// Walks every source share of one event and routes all media inside its capture window, in batches
    /// so metadata extraction stays bounded. Unlike a normal cycle this is not capped at
    /// MaxFilesPerSharePerCycle: it runs to completion, because it exists to apply an event that was
    /// defined after the media already arrived.
    /// </summary>
    private async Task<CycleResult> ProcessBackfillAsync(
        MomentFerryRuntimeSettings settings,
        Guid eventId,
        CancellationToken cancellationToken)
    {
        var mediaEvent = await events.GetAsync(eventId, cancellationToken);
        if (mediaEvent is null)
        {
            logger.LogWarning("Backfill skipped: event {EventId} no longer exists", eventId);
            return new CycleResult(0, 0, 0, 0, 0, 0);
        }

        var sourceGroup = await sourceGroups.GetAsync(mediaEvent.SourceGroupId, cancellationToken);
        if (sourceGroup is null)
        {
            logger.LogWarning("Backfill skipped: event {Event} has no source group", mediaEvent.Name);
            return new CycleResult(0, 0, 0, 0, 0, 1);
        }

        var sourceShares = (await shares.ListAsync(cancellationToken))
            .Where(x => sourceGroup.ShareIds.Contains(x.Id))
            .Where(x => x.Enabled && x.Role is ShareRole.Source or ShareRole.Both)
            .ToArray();

        var matched = 0;
        var wouldMove = 0;
        var executed = 0;
        var skipped = 0;
        var errors = 0;

        logger.LogInformation(
            "Backfill started for event {Event} ({Start} to {End}) across {Shares} source shares",
            mediaEvent.Name,
            mediaEvent.StartAt,
            mediaEvent.EndAt,
            sourceShares.Length);

        foreach (var sourceShare in sourceShares)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stableFiles = discovery.Enumerate(sourceShare)
                    .Where(x => x.State == DiscoveryState.Stable)
                    .ToArray();

                var processed = 0;
                var batchSize = Math.Clamp(settings.MaxFilesPerSharePerCycle, 1, 2000);
                foreach (var batch in stableFiles.Chunk(batchSize))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var offset = processed;
                    var items = await routing.EvaluateAsync(
                        sourceShare,
                        batch,
                        cancellationToken,
                        settings.MaxParallelMetadataReads,
                        progress => status.Progress(
                            sourceShare.Name,
                            $"Backfill: {mediaEvent.Name}",
                            offset + progress.Processed,
                            stableFiles.Length));

                    var tally = await RouteAsync(settings, items, mediaEvent.Id, cancellationToken);
                    matched += tally.Matched;
                    wouldMove += tally.WouldMove;
                    executed += tally.Executed;
                    skipped += tally.Skipped;
                    errors += tally.Errors;

                    processed += batch.Length;
                    status.Progress(sourceShare.Name, $"Backfill: {mediaEvent.Name}", processed, stableFiles.Length);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                errors++;
                logger.LogWarning(ex, "Backfill scan failed for share {Share}", sourceShare.Name);
            }
        }

        logger.LogInformation(
            "Backfill finished for event {Event}: {Matched} matched, {Executed} routed, {Skipped} skipped, {Errors} errors",
            mediaEvent.Name,
            matched,
            executed,
            skipped,
            errors);

        return new CycleResult(sourceShares.Length, matched, wouldMove, executed, skipped, errors);
    }

    /// <summary>
    /// Applies the routing decision to already-evaluated items. When <paramref name="onlyEventId"/> is
    /// set, items matching any other event are left alone, so an event backfill cannot move media that
    /// happens to fall inside a different event's window.
    /// </summary>
    private async Task<RouteTally> RouteAsync(
        MomentFerryRuntimeSettings settings,
        IReadOnlyList<RoutingPreviewItem> items,
        Guid? onlyEventId,
        CancellationToken cancellationToken)
    {
        var matched = 0;
        var wouldMove = 0;
        var executed = 0;
        var skipped = 0;
        var errors = 0;

        var routable = items
            .Where(x => x.State == RoutingPreviewState.Matched && x.Event is not null)
            .Where(x => onlyEventId is null || x.Event!.Id == onlyEventId.Value);

        foreach (var item in routable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            matched++;
            status.MatchFound();

            if (!settings.AllowFilesystemTimestampFallback &&
                string.Equals(item.MediaFile.TimestampSource, "FileLastWriteTimeUtc", StringComparison.Ordinal))
            {
                skipped++;
                logger.LogInformation(
                    "Auto-routing skipped {File}: capture time is filesystem fallback",
                    item.MediaFile.SourcePath);
                continue;
            }

            wouldMove++;
            status.WouldMoveFound();

            if (settings.DryRun)
            {
                skipped++;
                logger.LogInformation(
                    "Dry run: would route {Source} to {Destination} for event {Event}",
                    item.MediaFile.SourcePath,
                    item.DestinationPath,
                    item.Event!.Name);
                continue;
            }

            try
            {
                var result = await transfers.ExecuteOnceAsync(
                    item.MediaFile.Id,
                    item.Event!.Id,
                    cancellationToken);

                if (result.Executed)
                {
                    executed++;
                    logger.LogInformation(
                        "Auto-routing completed {Source}: {State}",
                        item.MediaFile.SourcePath,
                        result.Result?.Operation.State);
                }
                else
                {
                    skipped++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
            {
                errors++;
                logger.LogWarning(ex, "Auto-routing failed for {File}", item.MediaFile.SourcePath);
            }
        }

        return new RouteTally(matched, wouldMove, executed, skipped, errors);
    }

    /// <summary>
    /// Applies discovery rules to the watcher-reported paths. Paths that no longer exist, fall outside
    /// the share, or are not yet stable simply drop out and are picked up by the next full walk.
    /// </summary>
    private IReadOnlyList<DiscoveredFile> ObserveTargeted(Share sourceShare, IReadOnlyCollection<string> paths)
    {
        var observed = new List<DiscoveredFile>(paths.Count);
        foreach (var path in paths)
        {
            if (discovery.Observe(sourceShare, path) is { } file)
            {
                observed.Add(file);
            }
        }

        return observed;
    }

    private sealed record RouteTally(
        int Matched,
        int WouldMove,
        int Executed,
        int Skipped,
        int Errors);

    private sealed record CycleResult(
        int SourceShares,
        int Matched,
        int WouldMove,
        int Executed,
        int Skipped,
        int Errors);
}
