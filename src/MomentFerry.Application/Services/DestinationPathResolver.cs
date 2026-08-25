using MomentFerry.Application.Abstractions;
using MomentFerry.Core.Domain;

namespace MomentFerry.Application.Services;

public sealed class DestinationPathResolver(IFileSystemGateway? fileSystem = null)
{
    /// <summary>Upper bound on the sequence search, so a pathological folder cannot spin forever.</summary>
    private const int MaxSequence = 100000;

    public string Resolve(
        MediaEvent mediaEvent,
        Share sourceShare,
        Share destinationShare,
        MediaFile mediaFile,
        RenameContext? rename = null)
    {
        var captured = LocalCapture(mediaFile, sourceShare);
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
        var targetDirectory = Path.GetFullPath(Path.Combine(root, folder, mediaFolder));
        var fileName = ResolveFileName(
            mediaEvent,
            sourceShare,
            destinationShare,
            mediaFile,
            captured,
            targetDirectory,
            rename ?? RenameContext.Empty);

        var combined = Path.GetFullPath(Path.Combine(targetDirectory, fileName));
        EnsureInsideRoot(root, combined);
        return combined;
    }

    /// <summary>
    /// The capture time as wall-clock time where it was taken. <see cref="MediaFile.CapturedAt"/> is
    /// normalised to UTC so events and range queries compare one instant, but a name built from that
    /// carries a time the camera never showed: a photo taken at 13:52 in Berlin was stored as
    /// 20260821_115253. The offset the file reported is used when it exists, and otherwise the share's
    /// zone stands in — the same assumption the extractor already makes for a photo without one.
    /// </summary>
    private static DateTimeOffset LocalCapture(MediaFile mediaFile, Share sourceShare)
    {
        var captured = mediaFile.CapturedAt ?? DateTimeOffset.UtcNow;
        if (mediaFile.CapturedAtOffsetMinutes is { } minutes)
        {
            return captured.ToOffset(TimeSpan.FromMinutes(minutes));
        }

        try
        {
            var zoneId = string.IsNullOrWhiteSpace(sourceShare.DefaultTimeZone)
                ? TimeZoneInfo.Local.Id
                : sourceShare.DefaultTimeZone;
            var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
            return captured.ToOffset(zone.GetUtcOffset(captured));
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // A misconfigured zone must not stop a transfer; the instant is still a usable name.
            return captured;
        }
    }

    /// <summary>
    /// Applies the source preset first and then the destination preset to its result: the source
    /// normalizes what arrives, the destination shapes what is stored.
    /// </summary>
    private string ResolveFileName(
        MediaEvent mediaEvent,
        Share sourceShare,
        Share destinationShare,
        MediaFile mediaFile,
        DateTimeOffset captured,
        string targetDirectory,
        RenameContext rename)
    {
        var sourcePreset = rename.PresetFor(sourceShare);
        var destinationPreset = rename.PresetFor(destinationShare);
        var extension = Path.GetExtension(mediaFile.OriginalName);

        if (sourcePreset is null && destinationPreset is null) return mediaFile.OriginalName;

        var context = new FileNameContext(
            Path.GetFileNameWithoutExtension(mediaFile.OriginalName),
            captured,
            FileNameTemplate.ResolveCamera(mediaFile.CameraMake, mediaFile.CameraModel, rename.CameraNames),
            mediaFile.CameraMake,
            mediaFile.CameraModel,
            sourceShare.Name,
            sourceShare.Owner,
            mediaEvent.Name,
            mediaEvent.Type);

        var numbered = FileNameTemplate.UsesSequence(sourcePreset?.Template) ||
                       FileNameTemplate.UsesSequence(destinationPreset?.Template);

        for (var sequence = 1; sequence <= MaxSequence; sequence++)
        {
            var stem = context.Stem;
            if (sourcePreset is not null)
            {
                stem = FileNameTemplate.Render(sourcePreset.Template, context, sequence);
            }

            if (destinationPreset is not null)
            {
                stem = FileNameTemplate.Render(destinationPreset.Template, context with { Stem = stem }, sequence);
            }

            var candidate = stem + extension;
            // Without a sequence token there is nothing to vary, so the transfer's own conflict
            // strategy stays responsible for resolving a collision.
            if (!numbered || fileSystem is null) return candidate;
            if (!fileSystem.FileExists(Path.Combine(targetDirectory, candidate))) return candidate;
        }

        return mediaFile.OriginalName;
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
