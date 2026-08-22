namespace MomentFerry.Core.Domain;

/// <summary>
/// Built-in extension lists used when a share does not define its own. Shares store their own lists so
/// a device that only produces a subset can be narrowed without affecting other shares.
/// </summary>
public static class MediaExtensionDefaults
{
    public static IReadOnlyList<string> Images { get; } =
    [
        ".jpg", ".jpeg", ".png", ".heic", ".heif", ".webp", ".gif", ".tif", ".tiff",
        ".dng", ".arw", ".cr2", ".cr3", ".nef", ".raf"
    ];

    public static IReadOnlyList<string> Videos { get; } =
    [
        ".mp4", ".mov", ".m4v", ".avi", ".mkv", ".3gp", ".webm", ".mts", ".m2ts"
    ];

    /// <summary>
    /// Normalizes user input to the stored form: lower-cased, dot-prefixed, de-duplicated, order kept.
    /// An empty result means "use the built-in list", so clearing the field cannot silently stop
    /// discovery; excluding a media type entirely is done with the share's allowed media types.
    /// </summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string>? extensions)
    {
        if (extensions is null) return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var raw in extensions)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var trimmed = raw.Trim().TrimStart('*').Trim();
            if (trimmed.Length == 0) continue;
            var normalized = (trimmed.StartsWith('.') ? trimmed : "." + trimmed).ToLowerInvariant();
            if (normalized.Length < 2) continue;
            if (seen.Add(normalized)) result.Add(normalized);
        }

        return result;
    }
}
