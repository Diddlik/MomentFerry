using System.Collections.Concurrent;
using MomentFerry.Application.Abstractions;

namespace MomentFerry.Application.Services;

public sealed class TransferCoordinator(
    IMediaOperationRepository operations,
    IFileSystemGateway fileSystem,
    SafeTransferService transfer)
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _mediaLocks = new();

    public async Task<CoordinatedTransferResult> ExecuteOnceAsync(
        Guid mediaFileId,
        Guid eventId,
        CancellationToken cancellationToken = default)
    {
        var gate = _mediaLocks.GetOrAdd(mediaFileId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // A finished operation only blocks a re-route while the file it committed is still at the
            // destination. Trusting the record alone left media unroutable for good once its
            // destination was deleted afterwards: the source sat in the share, matched its event every
            // cycle, and was refused as "already routed" with nothing at the other end.
            if (await HasLiveDestinationAsync(mediaFileId, eventId, cancellationToken))
            {
                return new CoordinatedTransferResult(
                    false,
                    null,
                    "This media file/event combination has already reached a terminal operation state.",
                    AlreadyRouted: true);
            }

            var incomplete = await operations.GetIncompleteByMediaFileAsync(mediaFileId, cancellationToken);
            if (incomplete is not null)
            {
                return new CoordinatedTransferResult(
                    false,
                    null,
                    "This media file already has an incomplete operation. Recovery must resolve it first.");
            }

            var result = await transfer.ExecuteAsync(mediaFileId, eventId, cancellationToken);
            return new CoordinatedTransferResult(true, result, result.Message);
        }
        finally
        {
            gate.Release();
            if (gate.CurrentCount == 1)
            {
                _mediaLocks.TryRemove(new KeyValuePair<Guid, SemaphoreSlim>(mediaFileId, gate));
            }
        }
    }

    /// <summary>
    /// True when a finished operation of this media file/event pair still has its committed file at the
    /// destination. Existence is checked, not content: re-hashing every routed file on every cycle
    /// would read the whole library, and the transfer itself verifies bytes before it removes a source.
    /// </summary>
    private async Task<bool> HasLiveDestinationAsync(
        Guid mediaFileId,
        Guid eventId,
        CancellationToken cancellationToken)
    {
        foreach (var terminal in await operations.ListTerminalAsync(mediaFileId, eventId, cancellationToken))
        {
            if (!string.IsNullOrWhiteSpace(terminal.DestinationPath) &&
                fileSystem.FileExists(terminal.DestinationPath))
            {
                return true;
            }
        }

        return false;
    }
}

public sealed record CoordinatedTransferResult(
    bool Executed,
    TransferExecutionResult? Result,
    string? Message,
    // Set apart from the other refusals because a full share carries thousands of them every cycle:
    // the routing worker counts these instead of logging one line each.
    bool AlreadyRouted = false);
