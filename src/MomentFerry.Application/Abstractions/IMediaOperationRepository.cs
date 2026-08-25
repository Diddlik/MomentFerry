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
    /// Finds an operation that verified this exact content at a destination, ignoring the media file
    /// asking, and reports where it put it. The caller must still check that file on disk: this only
    /// says where to look. Superseded operations count, because a route-again marks every earlier
    /// operation of an event as superseded and that is exactly the moment its files are all
    /// transferred again.
    /// </summary>
    Task<MediaOperation?> FindByDestinationHashAsync(
        string destinationHash,
        Guid excludedMediaFileId,
        CancellationToken cancellationToken = default);

    Task<bool> HasTerminalOperationAsync(
Guid mediaFileId, Guid eventId, CancellationToken cancellationToken = default);
    /// <summary>
    /// Marks every finished operation of one event as superseded and reports how many were affected.
    /// This is what lifts the terminal-state block for a whole event, so its media can be routed again
    /// under changed rules or after the destination lost files.
    /// </summary>
    Task<int> SupersedeTerminalByEventAsync(
        Guid eventId,
        string reason,
        DateTimeOffset supersededAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes finished operations that completed before the cutoff. Only terminal states go: anything
    /// still waiting for a decision stays regardless of age.
    /// </summary>
    Task<int> DeleteFinishedBeforeAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken = default);

    Task UpsertAsync(MediaOperation operation, CancellationToken cancellationToken = default);
}
