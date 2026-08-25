namespace MomentFerry.Core.Domain;

public sealed class MediaFile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid SourceShareId { get; init; }
    public required string SourcePath { get; init; }
    public required string OriginalName { get; init; }
    public long Size { get; init; }
    public required string Extension { get; init; }
    public MediaType MediaType { get; init; }
    public DateTimeOffset? CapturedAt { get; init; }
    public string? TimestampSource { get; init; }

    /// <summary>
    /// The UTC offset the capture time was recorded in, when the file said so. <see cref="CapturedAt"/>
    /// is normalised to UTC for matching and range queries, which loses the offset; a filename has to
    /// carry the wall-clock time the camera wrote, so the offset is kept here rather than re-derived.
    /// Null means the file named no offset and the share's zone stands in.
    /// </summary>
    public int? CapturedAtOffsetMinutes { get; init; }
    public bool IsTimezoneInferred { get; init; }
    public string? Sha256 { get; init; }

    // Persisted so filename templates can use the camera on later cycles, which reuse indexed
    // metadata and therefore never run ExifTool again.
    public string? CameraMake { get; init; }
    public string? CameraModel { get; init; }
    public DateTimeOffset? SourceLastWriteAt { get; init; }
    public DateTimeOffset FirstSeenAt { get; init; }
    public DateTimeOffset LastSeenAt { get; init; }
}
