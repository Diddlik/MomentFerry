using MomentFerry.Application.Abstractions;
using MomentFerry.Core.Domain;

namespace MomentFerry.Application.Services;

public sealed class RoutingPreviewService(
    ShareDiscoveryService discovery,
    IMediaMetadataExtractor metadataExtractor,
    IMediaFileRepository mediaFiles,
    IMediaEventRepository events,
    ISourceGroupRepository sourceGroups,
    IShareRepository shares,
    DestinationPathResolver destinationPaths,
    RenameContextFactory renameContexts,
    IClock clock)
{
    /// <summary>
    /// Walks the whole share and evaluates the least-recently-seen slice of it. This is the periodic
    /// reconciliation path; watcher-driven work should use <see cref="EvaluateAsync"/> instead, which
    /// skips the walk.
    /// </summary>
    public async Task<IReadOnlyList<RoutingPreviewItem>> PreviewAsync(
        Share sourceShare,
        int limit,
        CancellationToken cancellationToken = default,
        int maxParallelMetadataReads = 1,
        Action<RoutingPreviewProgress>? progress = null)
    {
        var indexedFiles = (await mediaFiles.ListBySourceAsync(sourceShare.Id, cancellationToken))
            .ToDictionary(x => x.SourcePath, StringComparer.Ordinal);
        var stableFiles = discovery.Enumerate(sourceShare)
            .Where(x => x.State == DiscoveryState.Stable)
            .OrderBy(x => indexedFiles.TryGetValue(x.FullPath, out var indexed)
                ? indexed.LastSeenAt
                : DateTimeOffset.MinValue)
            .ThenBy(x => x.FullPath, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();

        return await EvaluateCoreAsync(
            sourceShare,
            stableFiles,
            indexedFiles,
            maxParallelMetadataReads,
            progress,
            cancellationToken);
    }

    /// <summary>
    /// Evaluates an explicit set of already-discovered files, without walking the share. Used for
    /// filesystem-watcher notifications, which already name the files that changed.
    /// </summary>
    public async Task<IReadOnlyList<RoutingPreviewItem>> EvaluateAsync(
        Share sourceShare,
        IReadOnlyList<DiscoveredFile> candidates,
        CancellationToken cancellationToken = default,
        int maxParallelMetadataReads = 1,
        Action<RoutingPreviewProgress>? progress = null)
    {
        var stableFiles = candidates
            .Where(x => x.State == DiscoveryState.Stable)
            .ToArray();
        if (stableFiles.Length == 0) return [];

        var indexedFiles = (await mediaFiles.ListBySourceAsync(sourceShare.Id, cancellationToken))
            .ToDictionary(x => x.SourcePath, StringComparer.Ordinal);

        return await EvaluateCoreAsync(
            sourceShare,
            stableFiles,
            indexedFiles,
            maxParallelMetadataReads,
            progress,
            cancellationToken);
    }

    private async Task<IReadOnlyList<RoutingPreviewItem>> EvaluateCoreAsync(
        Share sourceShare,
        DiscoveredFile[] stableFiles,
        Dictionary<string, MediaFile> indexedFiles,
        int maxParallelMetadataReads,
        Action<RoutingPreviewProgress>? progress,
        CancellationToken cancellationToken)
    {
        var metadata = new MediaMetadata?[stableFiles.Length];
        var cached = 0;
        var pending = new List<int>(stableFiles.Length);
        for (var index = 0; index < stableFiles.Length; index++)
        {
            var file = stableFiles[index];
            if (indexedFiles.TryGetValue(file.FullPath, out var existing) &&
                existing.Size == file.Size &&
                existing.SourceLastWriteAt == file.LastWriteUtc &&
                existing.CapturedAt is not null)
            {
                cached++;
            }
            else
            {
                pending.Add(index);
            }
        }

        progress?.Invoke(new RoutingPreviewProgress("Reading metadata", cached, stableFiles.Length));
        var metadataRead = 0;
        await Parallel.ForEachAsync(
            pending,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(maxParallelMetadataReads, 1, 8),
                CancellationToken = cancellationToken
            },
            async (index, ct) =>
            {
                var file = stableFiles[index];
                metadata[index] = await metadataExtractor.ExtractAsync(
                    sourceShare,
                    file.FullPath,
                    file.MediaType,
                    ct);
                var completed = cached + Interlocked.Increment(ref metadataRead);
                progress?.Invoke(new RoutingPreviewProgress("Reading metadata", completed, stableFiles.Length));
            });

        var rename = await renameContexts.LoadAsync(cancellationToken);
        var groups = (await sourceGroups.ListAsync(cancellationToken))
            .ToDictionary(x => x.Id);
        var allShares = (await shares.ListAsync(cancellationToken))
            .ToDictionary(x => x.Id);

        var result = new List<RoutingPreviewItem>(stableFiles.Length);
        for (var index = 0; index < stableFiles.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = stableFiles[index];
            indexedFiles.TryGetValue(file.FullPath, out var existing);
            var extracted = metadata[index];
            var capturedAt = extracted?.CapturedAt ?? existing?.CapturedAt ?? file.LastWriteUtc;
            var timestampSource = extracted?.TimestampSource ?? existing?.TimestampSource ?? "FileLastWriteTimeUtc";
            var fallbackMessage = extracted?.CapturedAt is null && extracted?.Error is not null
                ? $"Metadata unavailable; FileLastWriteTimeUtc used as fallback. {extracted.Error}"
                : null;
            var now = clock.UtcNow;

            var mediaFile = new MediaFile
            {
                Id = existing?.Id ?? Guid.NewGuid(),
                SourceShareId = sourceShare.Id,
                SourcePath = file.FullPath,
                OriginalName = Path.GetFileName(file.FullPath),
                Size = file.Size,
                Extension = Path.GetExtension(file.FullPath),
                MediaType = file.MediaType,
                CapturedAt = capturedAt,
                TimestampSource = timestampSource,
                // Kept beside the normalised instant so a filename can carry the wall-clock time the
                // camera wrote. An indexed value that predates this column has none, and the share's
                // zone stands in when the name is rendered.
                CapturedAtOffsetMinutes = extracted?.CapturedAt is { } fresh
                    ? (int)fresh.Offset.TotalMinutes
                    : existing?.CapturedAtOffsetMinutes,
                IsTimezoneInferred = extracted is null
                    ? existing?.IsTimezoneInferred ?? true
                    : extracted.TimeZoneInferred || extracted.CapturedAt is null,
                Sha256 = existing?.Sha256,
                CameraMake = extracted?.CameraMake ?? existing?.CameraMake,
                CameraModel = extracted?.CameraModel ?? existing?.CameraModel,
                SourceLastWriteAt = file.LastWriteUtc,
                FirstSeenAt = existing?.FirstSeenAt ?? now,
                LastSeenAt = now
            };
            await mediaFiles.UpsertAsync(mediaFile, cancellationToken);

            var candidateEvents = await events.ListMatchableAsync(capturedAt, cancellationToken);
            var matches = candidateEvents
                .Where(e => groups.TryGetValue(e.SourceGroupId, out var group) && group.ShareIds.Contains(sourceShare.Id))
                .ToArray();

            if (matches.Length == 0)
            {
                result.Add(new RoutingPreviewItem(mediaFile, RoutingPreviewState.Unmatched, null, null, fallbackMessage));
                progress?.Invoke(new RoutingPreviewProgress("Matching events", index + 1, stableFiles.Length));
                continue;
            }

            if (matches.Length > 1)
            {
                result.Add(new RoutingPreviewItem(
                    mediaFile,
                    RoutingPreviewState.Ambiguous,
                    null,
                    null,
                    $"File matches {matches.Length} events."));
                progress?.Invoke(new RoutingPreviewProgress("Matching events", index + 1, stableFiles.Length));
                continue;
            }

            var matchedEvent = matches[0];
            if (!allShares.TryGetValue(matchedEvent.DestinationShareId, out var destinationShare) ||
                !destinationShare.Enabled ||
                destinationShare.Role == ShareRole.Source)
            {
                result.Add(new RoutingPreviewItem(
                    mediaFile,
                    RoutingPreviewState.InvalidDestination,
                    matchedEvent,
                    null,
                    "Destination share is missing, disabled, or not writable by role."));
                progress?.Invoke(new RoutingPreviewProgress("Matching events", index + 1, stableFiles.Length));
                continue;
            }

            var destinationPath = destinationPaths.Resolve(matchedEvent, sourceShare, destinationShare, mediaFile, rename);
            result.Add(new RoutingPreviewItem(
                mediaFile,
                RoutingPreviewState.Matched,
                matchedEvent,
                destinationPath,
                fallbackMessage));
            progress?.Invoke(new RoutingPreviewProgress("Matching events", index + 1, stableFiles.Length));
        }

        return result;
    }
}

public sealed record RoutingPreviewProgress(string Phase, int Processed, int Total);

public sealed record RoutingPreviewItem(
    MediaFile MediaFile,
    RoutingPreviewState State,
    MediaEvent? Event,
    string? DestinationPath,
    string? Message);

public enum RoutingPreviewState
{
    Matched,
    Unmatched,
    Ambiguous,
    MetadataFallback,
    InvalidDestination
}
