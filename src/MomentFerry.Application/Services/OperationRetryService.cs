using MomentFerry.Application.Abstractions;
using MomentFerry.Core.Domain;

namespace MomentFerry.Application.Services;

public sealed class OperationRetryService(
    IMediaOperationRepository operations,
    IMediaEventRepository events,
    IShareRepository shares,
    IFileSystemGateway fileSystem,
    SafeTransferService transfer,
    IClock clock)
{
    public async Task<TransferExecutionResult> RetryAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        var operation = await operations.GetAsync(operationId, cancellationToken)
            ?? throw new InvalidOperationException("Operation does not exist.");

        if (operation.State is not (MediaOperationState.RetryPending or MediaOperationState.Quarantined))
            throw new InvalidOperationException("Only retry-pending or quarantined operations can be retried explicitly.");
        if (operation.EventId is null)
            throw new InvalidOperationException("Operation has no event and cannot be retried.");

        var mediaEvent = await events.GetAsync(operation.EventId.Value, cancellationToken)
            ?? throw new InvalidOperationException("Operation event no longer exists.");
        var destinationShare = await shares.GetAsync(mediaEvent.DestinationShareId, cancellationToken)
            ?? throw new InvalidOperationException("Destination share no longer exists.");

        if (!string.IsNullOrWhiteSpace(operation.StagingPath) && fileSystem.FileExists(operation.StagingPath))
        {
            var stagingRoot = Path.GetFullPath(Path.Combine(destinationShare.Path, ".momentferry-staging"))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var stagingPath = Path.GetFullPath(operation.StagingPath);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!stagingPath.StartsWith(stagingRoot + Path.DirectorySeparatorChar, comparison))
                throw new InvalidOperationException("Persisted staging path is outside the destination staging directory.");

            fileSystem.DeleteFile(stagingPath);
        }

        await operations.UpsertAsync(
            Supersede(operation, "Superseded by explicit retry."),
            cancellationToken);

        return await transfer.ExecuteAsync(operation.MediaFileId, operation.EventId.Value, cancellationToken);
    }

    /// <summary>
    /// Routes a file again that already reached a terminal state, so a changed rename preset or
    /// destination layout can be applied to it. The previous operation is marked superseded, which is
    /// what lifts the terminal-state block in <see cref="TransferCoordinator"/>. Any file the earlier
    /// run wrote is left where it is: removing it is the user's decision, not a side effect of this.
    /// </summary>
    public async Task<TransferExecutionResult> RouteAgainAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        var operation = await operations.GetAsync(operationId, cancellationToken)
            ?? throw new InvalidOperationException("Operation does not exist.");

        if (operation.State is not (MediaOperationState.Completed or MediaOperationState.Ignored))
            throw new InvalidOperationException("Only completed or ignored operations can be routed again. Use retry for the rest.");
        if (operation.EventId is null)
            throw new InvalidOperationException("Operation has no event and cannot be routed again.");
        if (!fileSystem.FileExists(operation.SourcePath))
            throw new FileNotFoundException("The source file no longer exists, so there is nothing to route again.", operation.SourcePath);

        await operations.UpsertAsync(
            Supersede(operation, "Superseded by an explicit route-again request."),
            cancellationToken);

        return await transfer.ExecuteAsync(operation.MediaFileId, operation.EventId.Value, cancellationToken);
    }

    private MediaOperation Supersede(MediaOperation operation, string reason) => new()
    {
        Id = operation.Id,
        MediaFileId = operation.MediaFileId,
        EventId = operation.EventId,
        State = MediaOperationState.Failed,
        SourcePath = operation.SourcePath,
        StagingPath = operation.StagingPath,
        DestinationPath = operation.DestinationPath,
        SourceHash = operation.SourceHash,
        DestinationHash = operation.DestinationHash,
        RetryCount = operation.RetryCount,
        LastError = reason,
        StartedAt = operation.StartedAt,
        CompletedAt = clock.UtcNow
    };
}
