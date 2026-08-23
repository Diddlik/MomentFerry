namespace MomentFerry.Core.Domain;

/// <summary>
/// Rewrites a raw EXIF camera model into a readable name, for example CPH2581 to OnePlus12, so the
/// {camera} token produces the name a person would recognize rather than a vendor part number.
/// </summary>
public sealed class CameraMapping
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Raw value as reported by the camera. Matched case-insensitively.</summary>
    public required string From { get; init; }

    public required string To { get; init; }
}
