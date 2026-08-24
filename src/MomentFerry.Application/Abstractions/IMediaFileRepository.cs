using MomentFerry.Core.Domain;

namespace MomentFerry.Application.Abstractions;

public interface IMediaFileRepository
{
    Task<IReadOnlyList<MediaFile>> ListRecentAsync(int limit = 200, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MediaFile>> ListBySourceAsync(Guid sourceShareId, CancellationToken cancellationToken = default);
    Task<MediaFile?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<MediaFile?> GetBySourceAsync(Guid sourceShareId, string sourcePath, CancellationToken cancellationToken = default);
    /// <summary>
    /// Clears the last-write stamp so the next cycle extracts metadata again instead of reusing the
    /// index. The capture time already on record is kept until the fresh read replaces it, so a failed
    /// extraction costs nothing. Pass null to cover every share.
    /// </summary>
    Task<int> ClearMetadataStampAsync(Guid? shareId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the named rows, skipping any that an operation still refers to. The foreign key would
    /// cascade the delete into the operation history, and that history is the record that a file was
    /// verified before its source was released.
    /// </summary>
    Task<int> DeleteUnreferencedAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default);

    Task UpsertAsync(MediaFile mediaFile, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pushes indexed files whose capture time falls inside an event window back to the front of the
    /// routing queue, so an event created or edited after the media arrived is applied on the next
    /// cycle instead of waiting for the least-recently-evaluated sweep to reach them.
    /// </summary>
    Task<int> RequeueByCaptureWindowAsync(
        IReadOnlyCollection<Guid> sourceShareIds,
        DateTimeOffset startAt,
        DateTimeOffset? endAt,
        CancellationToken cancellationToken = default);
}
