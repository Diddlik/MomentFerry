using MomentFerry.Core.Domain;

namespace MomentFerry.Application.Abstractions;

public interface IMediaMetadataExtractor
{
    Task<MediaMetadata> ExtractAsync(
        Share share,
        string path,
        MediaType mediaType,
        CancellationToken cancellationToken = default);
}

public sealed record MediaMetadata(
    DateTimeOffset? CapturedAt,
    string? TimestampSource,
    bool TimeZoneInferred,
    /// <summary>
    /// The offset the file itself stated, or null when it stated none. Distinct from
    /// <paramref name="TimeZoneInferred"/>: a QuickTime video's UTC timestamp is a certain instant
    /// whose wall-clock offset is unknown, and recording zero for it names the file in UTC.
    /// </summary>
    TimeSpan? ReportedUtcOffset,
    string? CameraMake,
    string? CameraModel,
    int? Width,
    int? Height,
    double? DurationSeconds,
    string? MimeType,
    string? Error = null);
