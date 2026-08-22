using MomentFerry.Core.Domain;

namespace MomentFerry.Application.Services;

public sealed class DestinationPathResolver
{
    public string Resolve(
        MediaEvent mediaEvent,
        Share sourceShare,
        Share destinationShare,
        MediaFile mediaFile)
    {
        var captured = mediaFile.CapturedAt ?? DateTimeOffset.UtcNow;
        var folder = mediaEvent.DestinationFolderTemplate
            .Replace("{event.name}", SafeSegment(mediaEvent.Name), StringComparison.OrdinalIgnoreCase)
            .Replace("{event.type}", SafeSegment(mediaEvent.Type ?? "Event"), StringComparison.OrdinalIgnoreCase)
            .Replace("{year}", captured.Year.ToString("0000"), StringComparison.OrdinalIgnoreCase)
            .Replace("{month}", captured.Month.ToString("00"), StringComparison.OrdinalIgnoreCase)
            .Replace("{day}", captured.Day.ToString("00"), StringComparison.OrdinalIgnoreCase)
            .Replace("{source}", SafeSegment(sourceShare.Name), StringComparison.OrdinalIgnoreCase)
            .Replace("{owner}", SafeSegment(sourceShare.Owner ?? sourceShare.Name), StringComparison.OrdinalIgnoreCase);

        // A destination may split media into subfolders below the event folder; unset keeps them together.
        var subfolder = destinationShare.SubfolderFor(mediaFile.MediaType);
        var mediaFolder = string.IsNullOrWhiteSpace(subfolder)
            ? string.Empty
            : string.Join(
                Path.DirectorySeparatorChar,
                subfolder.Replace('\\', '/')
                    .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(SafeSegment));

        var root = Path.GetFullPath(destinationShare.Path);
        var combined = Path.GetFullPath(Path.Combine(root, folder, mediaFolder, mediaFile.OriginalName));
        EnsureInsideRoot(root, combined);
        return combined;
    }

    public static void EnsureInsideRoot(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullCandidate = Path.GetFullPath(candidate);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = fullRoot + Path.DirectorySeparatorChar;

        if (!fullCandidate.StartsWith(prefix, comparison))
        {
            throw new InvalidOperationException("Destination path escapes the configured destination share.");
        }
    }

    public static string SafeSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var cleaned = new string(value.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        if (cleaned is "." or "..") return "_" + cleaned.Replace('.', '_');
        return string.IsNullOrWhiteSpace(cleaned) ? "unnamed" : cleaned;
    }
}
