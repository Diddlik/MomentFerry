using MomentFerry.Core.Domain;

namespace MomentFerry.Application.Abstractions;

public interface IMediaOperationRepository
{
    Task<IReadOnlyList<MediaOperation>> ListRecentAsync(int limit = 200, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MediaOperation>> ListByStateAsync(MediaOperationState state, int limit = 200, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<MediaOperationState, long>> CountByStateAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MediaOperation>> ListIncompleteAsync(CancellationToken cancellationToken = default);
    Task<MediaOperation?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<MediaOperation?> GetIncompleteByMediaFileAsync(Guid mediaFileId, CancellationToken cancellationToken = default);
    /// <summary>
    /// Finds a completed operation that wrote this exact content to a destination, ignoring the media
    /// file asking. A hit means the candidate is MomentFerry's own output arriving back on a source
    /// share, which must never be deleted as a duplicate of itself.
    /// </summary>
    Task<MediaOperation?> FindCompletedByDestinationHashAsync(
        string destinationHash,
        Guid excludedMediaFileId,
        CancellationToken cancellationToken = default);

    Task<bool> HasTerminalOperationAsync(
Guid mediaFileId, Guid eventId, CancellationToken cancellationToken = default);
    Task UpsertAsync(MediaOperation operation, CancellationToken cancellationToken = default);
}
