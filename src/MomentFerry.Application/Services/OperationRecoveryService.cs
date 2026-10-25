using Microsoft.Extensions.Logging;
using MomentFerry.Application.Abstractions;
using MomentFerry.Core.Domain;

namespace MomentFerry.Application.Services;

public sealed class OperationRecoveryService(
    IMediaOperationRepository operations,
    IMediaEventRepository events,
    IFileSystemGateway fileSystem,
    IHashService hashService,
    IClock clock,
    ILogger<OperationRecoveryService> logger)
{
    public async Task<RecoveryReport> RecoverAsync(CancellationToken cancellationToken = default)
    {
        var pending = await operations.ListIncompleteAsync(cancellationToken);
        var items = new List<RecoveryItem>(pending.Count);

        foreach (var operation in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!HasCommittedDestination(operation.State))
                {
                    var retry = Transition(
                        operation,
                        MediaOperationState.RetryPending,
                        "Recovered before destination commit; source preserved and explicit retry is required.");
                    await operations.UpsertAsync(retry, cancellationToken);
                    items.Add(new RecoveryItem(operation.Id, retry.State, retry.LastError));
                    continue;
                }

                items.Add(await RecoverCommittedAsync(operation, cancellationToken));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Recovery of operation {Operation} for {Source} failed", operation.Id, operation.SourcePath);
                var safeState = HasCommittedDestination(operation.State)
                    ? MediaOperationState.SourceFinalizePending
                    : MediaOperationState.RetryPending;
                var pendingState = Transition(operation, safeState, $"Recovery could not continue safely: {ex.Message}");
                await operations.UpsertAsync(pendingState, CancellationToken.None);
                items.Add(new RecoveryItem(operation.Id, pendingState.State, pendingState.LastError));
            }
        }

        foreach (var item in items)
        {
            logger.LogDebug("Recovery left operation {Operation} as {State}: {Message}", item.OperationId, item.State, item.Message);
        }

        return new RecoveryReport(
            items.Count,
            items.Count(x => x.State == MediaOperationState.Completed),
            items.Count(x => x.State == MediaOperationState.Quarantined),
            items.Count(x => x.State == MediaOperationState.RetryPending),
            items);
    }

    private async Task<RecoveryItem> RecoverCommittedAsync(
        MediaOperation operation,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(operation.DestinationPath) ||
            !fileSystem.FileExists(operation.DestinationPath))
        {
            var quarantined = Transition(operation, MediaOperationState.Quarantined, "Committed destination is missing during recovery.");
            await operations.UpsertAsync(quarantined, cancellationToken);
            return new RecoveryItem(operation.Id, quarantined.State, quarantined.LastError);
        }

        var destinationHash = await HashPathAsync(operation.DestinationPath, cancellationToken);
        var expectedHash = operation.DestinationHash ?? operation.SourceHash;
        if (string.IsNullOrWhiteSpace(expectedHash) ||
            !string.Equals(expectedHash, destinationHash, StringComparison.OrdinalIgnoreCase))
        {
            var quarantined = Transition(operation, MediaOperationState.Quarantined, "Committed destination hash cannot be verified during recovery.");
            await operations.UpsertAsync(quarantined, cancellationToken);
            return new RecoveryItem(operation.Id, quarantined.State, quarantined.LastError);
        }

        if (operation.EventId is null)
        {
            var quarantined = Transition(operation, MediaOperationState.Quarantined, "Operation event no longer exists; source will not be deleted automatically.");
            await operations.UpsertAsync(quarantined, cancellationToken);
            return new RecoveryItem(operation.Id, quarantined.State, quarantined.LastError);
        }

        var mediaEvent = await events.GetAsync(operation.EventId.Value, cancellationToken);
        if (mediaEvent is null)
        {
            var quarantined = Transition(operation, MediaOperationState.Quarantined, "Operation event cannot be loaded; source will not be deleted automatically.");
            await operations.UpsertAsync(quarantined, cancellationToken);
            return new RecoveryItem(operation.Id, quarantined.State, quarantined.LastError);
        }

        if (mediaEvent.OperationMode != OperationMode.SafeMove)
        {
            var completedCopy = Transition(operation, MediaOperationState.Completed, null, clock.UtcNow);
            await operations.UpsertAsync(completedCopy, cancellationToken);
            return new RecoveryItem(operation.Id, completedCopy.State, "Destination verified; copy operation completed without source deletion.");
        }

        if (fileSystem.FileExists(operation.SourcePath))
        {
            var sourceHash = await HashPathAsync(operation.SourcePath, cancellationToken);
            if (!string.Equals(sourceHash, destinationHash, StringComparison.OrdinalIgnoreCase))
            {
                var quarantined = Transition(operation, MediaOperationState.Quarantined, "Source no longer matches committed destination; source preserved.");
                await operations.UpsertAsync(quarantined, cancellationToken);
                return new RecoveryItem(operation.Id, quarantined.State, quarantined.LastError);
            }

            var finalize = Transition(operation, MediaOperationState.SourceFinalizePending);
            await operations.UpsertAsync(finalize, cancellationToken);
            fileSystem.DeleteFile(operation.SourcePath);
        }

        var completed = Transition(operation, MediaOperationState.Completed, null, clock.UtcNow);
        await operations.UpsertAsync(completed, cancellationToken);
        return new RecoveryItem(operation.Id, completed.State, "Committed destination verified and safe move recovery completed.");
    }

    private async Task<string> HashPathAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = fileSystem.OpenRead(path);
        return await hashService.ComputeSha256Async(stream, cancellationToken);
    }

    private static bool HasCommittedDestination(MediaOperationState state) =>
        state is MediaOperationState.DestinationCommitted or MediaOperationState.SourceFinalizePending;

    private static MediaOperation Transition(
        MediaOperation source,
        MediaOperationState state,
        string? lastError = null,
        DateTimeOffset? completedAt = null) => new()
    {
        Id = source.Id,
        MediaFileId = source.MediaFileId,
        EventId = source.EventId,
        State = state,
        SourcePath = source.SourcePath,
        StagingPath = source.StagingPath,
        DestinationPath = source.DestinationPath,
        SourceHash = source.SourceHash,
        DestinationHash = source.DestinationHash,
        RetryCount = source.RetryCount,
        LastError = lastError,
        StartedAt = source.StartedAt,
        CompletedAt = completedAt ?? source.CompletedAt
    };
}

public sealed record RecoveryReport(
    int Total,
    int Completed,
    int Quarantined,
    int RetryPending,
    IReadOnlyList<RecoveryItem> Items);

public sealed record RecoveryItem(Guid OperationId, MediaOperationState State, string? Message);
