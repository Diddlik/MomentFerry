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
            // A finished operation does not block a re-route. Whether the file still needs to move is
            // a question about bytes, and only the transfer can answer it: it finds the content by
            // hash wherever it was stored and lets the event's DuplicateStrategy decide, and it gives
            // differing content a name of its own through the ConflictStrategy. A guard that asked
            // instead whether some file occupies the recorded destination path answered a different
            // question, and stranded a source for good once anything else came to sit under that name.
            var incomplete = await operations.GetIncompleteByMediaFileAsync(mediaFileId, cancellationToken);
            if (incomplete is not null)
            {
                return new CoordinatedTransferResult(
                    false,
                    null,
                    "This media file already has an incomplete operation. Recovery must resolve it first.");
            }

            try
            {
                var result = await transfer.ExecuteAsync(mediaFileId, eventId, cancellationToken);
                return new CoordinatedTransferResult(true, result, result.Message);
            }
            catch (FileNotFoundException ex)
            {
                // Nothing left to move: an earlier pass already released this source. With no
                // terminal-state guard in front, this is the ordinary way a second attempt on the
                // same file ends, so it is a refusal and not a transfer error.
                return new CoordinatedTransferResult(false, null, ex.Message);
            }
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
    string? Message);
