using System.Collections.Concurrent;
using MomentFerry.Application.Abstractions;

namespace MomentFerry.Application.Services;

public sealed class TransferCoordinator(
    IMediaOperationRepository operations,
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
            if (await operations.HasTerminalOperationAsync(mediaFileId, eventId, cancellationToken))
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
}

public sealed record CoordinatedTransferResult(
    bool Executed,
    TransferExecutionResult? Result,
    string? Message,
    // Set apart from the other refusals because a full share carries thousands of them every cycle:
    // the routing worker counts these instead of logging one line each.
    bool AlreadyRouted = false);
