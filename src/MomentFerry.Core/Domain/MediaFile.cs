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
