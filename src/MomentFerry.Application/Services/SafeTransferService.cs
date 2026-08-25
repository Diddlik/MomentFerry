using MomentFerry.Application.Abstractions;
using MomentFerry.Core.Domain;

namespace MomentFerry.Application.Services;

public sealed class SafeTransferService(
    IMediaFileRepository mediaFiles,
    IMediaOperationRepository operations,
    IMediaEventRepository events,
    ISourceGroupRepository sourceGroups,
    IShareRepository shares,
    IFileSystemGateway fileSystem,
    IHashService hashService,
    DestinationPathResolver destinationPaths,
    RenameContextFactory renameContexts,
    IClock clock)
{
    public async Task<TransferExecutionResult> ExecuteAsync(
        Guid mediaFileId,
        Guid eventId,
        CancellationToken cancellationToken = default)
    {
        var mediaFile = await mediaFiles.GetAsync(mediaFileId, cancellationToken)
            ?? throw new InvalidOperationException("Media file does not exist in the MomentFerry index.");
        var mediaEvent = await events.GetAsync(eventId, cancellationToken)
            ?? throw new InvalidOperationException("Event does not exist.");
        var sourceShare = await shares.GetAsync(mediaFile.SourceShareId, cancellationToken)
            ?? throw new InvalidOperationException("Source share does not exist.");
        var destinationShare = await shares.GetAsync(mediaEvent.DestinationShareId, cancellationToken)
            ?? throw new InvalidOperationException("Destination share does not exist.");
        var sourceGroup = await sourceGroups.GetAsync(mediaEvent.SourceGroupId, cancellationToken)
            ?? throw new InvalidOperationException("Source group does not exist.");

        ValidateRoute(mediaFile, mediaEvent, sourceShare, destinationShare, sourceGroup);

        var incomplete = await operations.GetIncompleteByMediaFileAsync(mediaFile.Id, cancellationToken);
        if (incomplete is not null)
        {
            return new TransferExecutionResult(
                incomplete,
                false,
                HasCommittedDestination(incomplete.State),
                "An incomplete operation already exists for this media file. Recovery must resolve it first.");
        }

        if (!fileSystem.FileExists(mediaFile.SourcePath))
            throw new FileNotFoundException("Source file no longer exists.", mediaFile.SourcePath);

        var currentSize = fileSystem.GetFileLength(mediaFile.SourcePath);
        if (currentSize != mediaFile.Size)
            throw new IOException("Source file size changed after discovery; refusing to transfer it.");

        if (mediaEvent.OperationMode == OperationMode.Archive)
            throw new NotSupportedException("Archive retention is not implemented yet. Use Copy or SafeMove.");

        var operationId = Guid.NewGuid();
        var rename = await renameContexts.LoadAsync(cancellationToken);
        var desiredDestination = destinationPaths.Resolve(mediaEvent, sourceShare, destinationShare, mediaFile, rename);
        var stagingDirectory = Path.Combine(destinationShare.Path, ".momentferry-staging");
        DestinationPathResolver.EnsureInsideRoot(destinationShare.Path, Path.Combine(stagingDirectory, "probe"));
        fileSystem.EnsureDirectory(stagingDirectory);
        var stagingPath = Path.Combine(stagingDirectory, operationId.ToString("N") + mediaFile.Extension + ".part");

        var operation = new MediaOperation
        {
            Id = operationId,
            MediaFileId = mediaFile.Id,
            EventId = mediaEvent.Id,
            State = MediaOperationState.TransferPending,
            SourcePath = mediaFile.SourcePath,
            StagingPath = stagingPath,
            DestinationPath = desiredDestination,
            StartedAt = clock.UtcNow
        };
        await operations.UpsertAsync(operation, cancellationToken);

        try
        {
            var sourceHash = await HashPathAsync(mediaFile.SourcePath, cancellationToken);
            await PersistMediaHashAsync(mediaFile, sourceHash, cancellationToken);

            // The destination may already hold this content under a different name: the name a file
            // renders to now need not be the name it was stored under, because a changed preset, a
            // changed camera mapping or a taken sequence number all produce a different one. The
            // history is only asked where the content went; the file there decides, by hash.
            var storedCopy = await FindStoredCopyAsync(mediaFile.Id, sourceHash, cancellationToken);
            if (storedCopy is not null)
            {
                var handled = await HandleIdenticalDestinationAsync(
                    operation,
                    storedCopy,
                    sourceHash,
                    mediaEvent,
                    cancellationToken);
                if (handled is not null) return handled;
            }

            var conflict = await ResolveExistingDestinationAsync(
                desiredDestination,
                sourceHash,
                mediaEvent,
                sourceShare,
                cancellationToken);

            if (conflict.ExistingIdentical)
            {
                var handled = await HandleIdenticalDestinationAsync(
                    operation,
                    conflict.Path,
                    sourceHash,
                    mediaEvent,
                    cancellationToken);
                if (handled is not null) return handled;
            }

            var finalDestination = conflict.Path;
            operation = Transition(
                operation,
                MediaOperationState.Copying,
                destinationPath: finalDestination,
                sourceHash: sourceHash);
            await operations.UpsertAsync(operation, cancellationToken);

            await fileSystem.CopyFileAsync(mediaFile.SourcePath, stagingPath, cancellationToken);

            operation = Transition(operation, MediaOperationState.Verifying);
            await operations.UpsertAsync(operation, cancellationToken);

            if (!fileSystem.FileExists(stagingPath) || fileSystem.GetFileLength(stagingPath) != currentSize)
            {
                operation = Transition(operation, MediaOperationState.Quarantined, lastError: "Staging file size does not match source.");
                await operations.UpsertAsync(operation, cancellationToken);
                return new TransferExecutionResult(operation, false, false, operation.LastError);
            }

            var stagedHash = await HashPathAsync(stagingPath, cancellationToken);
            var sourceHashAfterCopy = await HashPathAsync(mediaFile.SourcePath, cancellationToken);
            if (!string.Equals(sourceHash, stagedHash, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(sourceHash, sourceHashAfterCopy, StringComparison.OrdinalIgnoreCase))
            {
                operation = Transition(
                    operation,
                    MediaOperationState.Quarantined,
                    sourceHash: sourceHashAfterCopy,
                    destinationHash: stagedHash,
                    lastError: "SHA-256 verification failed or source changed during copy.");
                await operations.UpsertAsync(operation, cancellationToken);
                return new TransferExecutionResult(operation, false, false, operation.LastError);
            }

            var commitConflict = await ResolveExistingDestinationAsync(
                finalDestination,
                sourceHash,
                mediaEvent,
                sourceShare,
                cancellationToken);

            if (commitConflict.ExistingIdentical)
            {
                fileSystem.DeleteFile(stagingPath);
                var handled = await HandleIdenticalDestinationAsync(
                    operation,
                    commitConflict.Path,
                    sourceHash,
                    mediaEvent,
                    cancellationToken);
                if (handled is not null) return handled;
                finalDestination = commitConflict.Path;
            }
            else
            {
                finalDestination = commitConflict.Path;
                fileSystem.MoveFile(stagingPath, finalDestination);
            }

            if (!fileSystem.FileExists(finalDestination) || fileSystem.GetFileLength(finalDestination) != currentSize)
            {
                operation = Transition(operation, MediaOperationState.Quarantined, destinationPath: finalDestination, lastError: "Final destination verification failed.");
                await operations.UpsertAsync(operation, cancellationToken);
                return new TransferExecutionResult(operation, false, false, operation.LastError);
            }

            var finalHash = await HashPathAsync(finalDestination, cancellationToken);
            if (!string.Equals(sourceHash, finalHash, StringComparison.OrdinalIgnoreCase))
            {
                operation = Transition(
                    operation,
                    MediaOperationState.Quarantined,
                    destinationPath: finalDestination,
                    destinationHash: finalHash,
                    lastError: "Final destination SHA-256 does not match source.");
                await operations.UpsertAsync(operation, cancellationToken);
                return new TransferExecutionResult(operation, false, true, operation.LastError);
            }

            // Only after the destination is verified byte for byte: a stamped timestamp must never be
            // the reason a transfer is judged complete, and a filesystem that refuses the stamp must not
            // undo a good copy.
            var stampMessage = ApplyCaptureTimestamp(finalDestination, mediaFile.CapturedAt!.Value);

            operation = Transition(
                operation,
                MediaOperationState.DestinationCommitted,
                destinationPath: finalDestination,
                sourceHash: sourceHash,
                destinationHash: finalHash);
            await operations.UpsertAsync(operation, cancellationToken);

            var deleted = false;
            if (mediaEvent.OperationMode == OperationMode.SafeMove)
            {
                operation = Transition(operation, MediaOperationState.SourceFinalizePending);
                await operations.UpsertAsync(operation, cancellationToken);
                fileSystem.DeleteFile(mediaFile.SourcePath);
                deleted = true;
            }

            operation = Transition(operation, MediaOperationState.Completed, completedAt: clock.UtcNow);
            await operations.UpsertAsync(operation, cancellationToken);
            return new TransferExecutionResult(operation, deleted, true, stampMessage);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DestinationConflictException ex)
        {
            operation = Transition(operation, MediaOperationState.Quarantined, lastError: ex.Message);
            await operations.UpsertAsync(operation, CancellationToken.None);
            return new TransferExecutionResult(operation, false, false, ex.Message);
        }
        catch (Exception ex)
        {
            operation = Transition(
                operation,
                HasCommittedDestination(operation.State)
                    ? MediaOperationState.SourceFinalizePending
                    : MediaOperationState.RetryPending,
                retryCount: operation.RetryCount + 1,
                lastError: ex.Message);
            await operations.UpsertAsync(operation, CancellationToken.None);
            throw;
        }
    }

    private async Task<TransferExecutionResult?> HandleIdenticalDestinationAsync(
        MediaOperation operation,
        string existingPath,
        string sourceHash,
        MediaEvent mediaEvent,
        CancellationToken cancellationToken)
    {
        if (mediaEvent.DuplicateStrategy == DuplicateStrategy.KeepBoth)
            return null;

        if (mediaEvent.DuplicateStrategy != DuplicateStrategy.SafeMoveToExisting)
        {
            var ignored = Transition(
                operation,
                MediaOperationState.Ignored,
                destinationPath: existingPath,
                sourceHash: sourceHash,
                destinationHash: sourceHash,
                lastError: "Identical destination already exists; source preserved by duplicate policy.",
                completedAt: clock.UtcNow);
            await operations.UpsertAsync(ignored, cancellationToken);
            return new TransferExecutionResult(ignored, false, false, "Identical destination exists; source preserved.");
        }

        var committed = Transition(
            operation,
            MediaOperationState.DestinationCommitted,
            destinationPath: existingPath,
            sourceHash: sourceHash,
            destinationHash: sourceHash);
        await operations.UpsertAsync(committed, cancellationToken);

        var sourceDeleted = false;
        if (mediaEvent.OperationMode == OperationMode.SafeMove)
        {
            committed = Transition(committed, MediaOperationState.SourceFinalizePending);
            await operations.UpsertAsync(committed, cancellationToken);
            try
            {
                fileSystem.DeleteFile(operation.SourcePath);
                sourceDeleted = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var pending = Transition(
                    committed,
                    MediaOperationState.SourceFinalizePending,
                    retryCount: committed.RetryCount + 1,
                    lastError: ex.Message);
                await operations.UpsertAsync(pending, CancellationToken.None);
                return new TransferExecutionResult(
                    pending,
                    false,
                    false,
                    "Destination is committed and verified; source deletion is pending recovery.");
            }
        }

        var completed = Transition(committed, MediaOperationState.Completed, completedAt: clock.UtcNow);
        await operations.UpsertAsync(completed, cancellationToken);
        return new TransferExecutionResult(completed, sourceDeleted, false, "Identical destination already existed and was verified by SHA-256.");
    }

    /// <summary>
    /// Sets the routed file's timestamps to the capture time so galleries that sort by file date match
    /// the order the media was shot in. Returns the reason when the filesystem refused, because a
    /// verified copy is worth keeping even without its stamp.
    /// </summary>
    private string? ApplyCaptureTimestamp(string path, DateTimeOffset capturedAt)
    {
        try
        {
            fileSystem.SetFileTimestampsUtc(path, capturedAt);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException or PlatformNotSupportedException)
        {
            return $"Destination is verified, but its capture timestamp could not be applied: {ex.Message}";
        }
    }

    private static void ValidateRoute(
        MediaFile mediaFile,
        MediaEvent mediaEvent,
        Share sourceShare,
        Share destinationShare,
        SourceGroup sourceGroup)
    {
        if (!sourceShare.Enabled || sourceShare.Role == ShareRole.Destination)
            throw new InvalidOperationException("Source share is not enabled for source processing.");
        if (!destinationShare.Enabled || destinationShare.Role == ShareRole.Source)
            throw new InvalidOperationException("Destination share is not enabled for destination writes.");
        if (!sourceGroup.ShareIds.Contains(sourceShare.Id))
            throw new InvalidOperationException("Source share is not part of the event's source group.");
        if (mediaFile.CapturedAt is null)
            throw new InvalidOperationException("Media file has no capture timestamp.");
        if (mediaEvent.Status is not (MediaEventStatus.Active or MediaEventStatus.Closed))
            throw new InvalidOperationException("Only active or closed events accept routed media.");
        if (mediaFile.CapturedAt < mediaEvent.StartAt ||
            (mediaEvent.EndAt is not null && mediaFile.CapturedAt > mediaEvent.EndAt))
            throw new InvalidOperationException("Media capture time is outside the event window.");
    }

    /// <summary>
    /// Reports the destination that already holds this exact content, under whatever name, or null.
    /// The operation history only supplies the path to look at: the file there is stat'ed and re-hashed,
    /// so a destination copy that was renamed, changed or lost never stops a transfer. What happens to
    /// the source is then the event's duplicate policy, not this method's decision.
    /// </summary>
    private async Task<string?> FindStoredCopyAsync(
        Guid mediaFileId,
        string sourceHash,
        CancellationToken cancellationToken)
    {
        var stored = await operations.FindByDestinationHashAsync(sourceHash, mediaFileId, cancellationToken);
        if (stored?.DestinationPath is null || !fileSystem.FileExists(stored.DestinationPath)) return null;

        var storedHash = await HashPathAsync(stored.DestinationPath, cancellationToken);
        return string.Equals(storedHash, sourceHash, StringComparison.OrdinalIgnoreCase)
            ? stored.DestinationPath
            : null;
    }

    private async Task<DestinationConflict> ResolveExistingDestinationAsync(
        string requestedPath,
        string sourceHash,
        MediaEvent mediaEvent,
        Share sourceShare,
        CancellationToken cancellationToken)
    {
        var candidate = requestedPath;
        var counter = 2;
        var sourceSuffixApplied = false;

        while (fileSystem.FileExists(candidate))
        {
            var existingHash = await HashPathAsync(candidate, cancellationToken);
            if (string.Equals(existingHash, sourceHash, StringComparison.OrdinalIgnoreCase) &&
                mediaEvent.DuplicateStrategy != DuplicateStrategy.KeepBoth)
            {
                return new DestinationConflict(candidate, true);
            }

            if (mediaEvent.ConflictStrategy == ConflictStrategy.Quarantine)
                throw new DestinationConflictException($"Destination conflict at '{candidate}' and conflict strategy is Quarantine.");

            var directory = Path.GetDirectoryName(requestedPath)!;
            var extension = Path.GetExtension(requestedPath);
            var stem = Path.GetFileNameWithoutExtension(requestedPath);

            if (mediaEvent.ConflictStrategy == ConflictStrategy.AppendSourceName && !sourceSuffixApplied)
            {
                candidate = Path.Combine(directory, $"{stem}_{DestinationPathResolver.SafeSegment(sourceShare.Name)}{extension}");
                sourceSuffixApplied = true;
            }
            else
            {
                var baseStem = sourceSuffixApplied && mediaEvent.ConflictStrategy == ConflictStrategy.AppendSourceName
                    ? $"{stem}_{DestinationPathResolver.SafeSegment(sourceShare.Name)}"
                    : stem;
                candidate = Path.Combine(directory, $"{baseStem}_{counter:00}{extension}");
                counter++;
            }
        }

        return new DestinationConflict(candidate, false);
    }

    private async Task<string> HashPathAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = fileSystem.OpenRead(path);
        return await hashService.ComputeSha256Async(stream, cancellationToken);
    }

    private async Task PersistMediaHashAsync(MediaFile mediaFile, string hash, CancellationToken cancellationToken)
    {
        if (string.Equals(mediaFile.Sha256, hash, StringComparison.OrdinalIgnoreCase)) return;
        await mediaFiles.UpsertAsync(new MediaFile
        {
            Id = mediaFile.Id,
            SourceShareId = mediaFile.SourceShareId,
            SourcePath = mediaFile.SourcePath,
            OriginalName = mediaFile.OriginalName,
            Size = mediaFile.Size,
            Extension = mediaFile.Extension,
            MediaType = mediaFile.MediaType,
            CapturedAt = mediaFile.CapturedAt,
            TimestampSource = mediaFile.TimestampSource,
            IsTimezoneInferred = mediaFile.IsTimezoneInferred,
            Sha256 = hash,
            FirstSeenAt = mediaFile.FirstSeenAt,
            LastSeenAt = clock.UtcNow
        }, cancellationToken);
    }

    private static bool HasCommittedDestination(MediaOperationState state) =>
        state is MediaOperationState.DestinationCommitted or
            MediaOperationState.SourceFinalizePending or
            MediaOperationState.Completed;

    private static MediaOperation Transition(
        MediaOperation source,
        MediaOperationState state,
        string? destinationPath = null,
        string? sourceHash = null,
        string? destinationHash = null,
        int? retryCount = null,
        string? lastError = null,
        DateTimeOffset? completedAt = null) => new()
    {
        Id = source.Id,
        MediaFileId = source.MediaFileId,
        EventId = source.EventId,
        State = state,
        SourcePath = source.SourcePath,
        StagingPath = source.StagingPath,
        DestinationPath = destinationPath ?? source.DestinationPath,
        SourceHash = sourceHash ?? source.SourceHash,
        DestinationHash = destinationHash ?? source.DestinationHash,
        RetryCount = retryCount ?? source.RetryCount,
        LastError = lastError,
        StartedAt = source.StartedAt,
        CompletedAt = completedAt ?? source.CompletedAt
    };

    private sealed record DestinationConflict(string Path, bool ExistingIdentical);
    private sealed class DestinationConflictException(string message) : IOException(message);
}

public sealed record TransferExecutionResult(
    MediaOperation Operation,
    bool SourceDeleted,
    bool DestinationCreated,
    string? Message);
