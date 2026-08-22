using System.Collections.Concurrent;
using System.IO.Enumeration;
using MomentFerry.Application.Abstractions;
using MomentFerry.Core.Domain;

namespace MomentFerry.Application.Services;

public sealed class ShareDiscoveryService(IFileSystemGateway fileSystem, IClock clock)
{
    private readonly ConcurrentDictionary<string, Observation> _observations = new(StringComparer.Ordinal);

    public IReadOnlyList<DiscoveredFile> Scan(Share share, int limit = 500)
        => Enumerate(share).Take(limit).ToList();

    /// <summary>Lazily walks the share so callers can either sample it or count all of it.</summary>
    public IEnumerable<DiscoveredFile> Enumerate(Share share)
    {
        // Guarded once per walk: ObserveCore must stay free of per-file directory syscalls.
        if (!IsWatchable(share))
        {
            yield break;
        }

        foreach (var path in fileSystem.EnumerateFiles(share.Path, share.Recursive))
        {
            if (ObserveCore(share, path) is { } discovered)
            {
                yield return discovered;
            }
        }
    }

    /// <summary>
    /// Applies the discovery rules to a single path. Used for filesystem-watcher notifications, which
    /// already name the changed file and so must not pay for a full share walk.
    /// </summary>
    public DiscoveredFile? Observe(Share share, string path)
    {
        if (!IsWatchable(share)) return null;

        var relativePath = Path.GetRelativePath(share.Path, path).Replace('\\', '/');
        if (relativePath.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relativePath))
        {
            // A watcher event for a path outside the share must never be routed as if it belonged to it.
            return null;
        }

        return ObserveCore(share, path);
    }

    private DiscoveredFile? ObserveCore(Share share, string path)
    {
        var relativePath = Path.GetRelativePath(share.Path, path).Replace('\\', '/');
        if (IsIgnored(relativePath, share.IgnorePatterns))
        {
            return null;
        }

        var mediaType = GetMediaType(share, path);
        if (mediaType == MediaType.Other || !share.AllowedMediaTypes.Contains(mediaType))
        {
            return null;
        }

        long size;
        DateTimeOffset lastWrite;
        try
        {
            size = fileSystem.GetFileLength(path);
            lastWrite = fileSystem.GetLastWriteTimeUtc(path);
        }
        catch (IOException)
        {
            return null;
        }

        var now = clock.UtcNow;
        var key = Path.GetFullPath(path);
        var observation = _observations.AddOrUpdate(
            key,
            _ => new Observation(size, lastWrite, now),
            (_, previous) => previous.Size == size && previous.LastWriteUtc == lastWrite
                ? previous
                : new Observation(size, lastWrite, now));

        var stable = now - observation.UnchangedSince >= TimeSpan.FromSeconds(share.StabilitySeconds);

        return new DiscoveredFile(
            path,
            relativePath,
            mediaType,
            size,
            lastWrite,
            observation.UnchangedSince,
            stable ? DiscoveryState.Stable : DiscoveryState.WaitingStable);
    }

    private bool IsWatchable(Share share)
        => share.Enabled && share.Role != ShareRole.Destination && fileSystem.DirectoryExists(share.Path);

    private static MediaType GetMediaType(Share share, string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Length == 0) return MediaType.Other;
        if (Matches(share.EffectiveImageExtensions, extension)) return MediaType.Image;
        if (Matches(share.EffectiveVideoExtensions, extension)) return MediaType.Video;
        return MediaType.Other;
    }

    private static bool Matches(IReadOnlyList<string> extensions, string extension)
    {
        for (var index = 0; index < extensions.Count; index++)
        {
            if (string.Equals(extensions[index], extension, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static bool IsIgnored(string relativePath, IReadOnlyList<string> patterns)
    {
        if (relativePath.StartsWith(".momentferry-staging/", StringComparison.Ordinal) ||
            string.Equals(relativePath, ".momentferry-staging", StringComparison.Ordinal) ||
            relativePath.StartsWith("@eaDir/", StringComparison.Ordinal) ||
            relativePath.Contains("/@eaDir/", StringComparison.Ordinal))
        {
            return true;
        }

        var fileName = Path.GetFileName(relativePath);
        foreach (var rawPattern in patterns)
        {
            var pattern = rawPattern.Trim().Replace('\\', '/');
            if (pattern.Length == 0) continue;

            if (pattern.EndsWith("/**", StringComparison.Ordinal))
            {
                var prefix = pattern[..^3].TrimEnd('/');
                if (relativePath.Equals(prefix, StringComparison.Ordinal) ||
                    relativePath.StartsWith(prefix + "/", StringComparison.Ordinal))
                {
                    return true;
                }
                continue;
            }

            if (FileSystemName.MatchesSimpleExpression(pattern, relativePath, ignoreCase: false) ||
                FileSystemName.MatchesSimpleExpression(pattern, fileName, ignoreCase: false))
            {
                return true;
            }
        }

        return false;
    }

    private sealed record Observation(long Size, DateTimeOffset LastWriteUtc, DateTimeOffset UnchangedSince);
}

public sealed record DiscoveredFile(
    string FullPath,
    string RelativePath,
    MediaType MediaType,
    long Size,
    DateTimeOffset LastWriteUtc,
    DateTimeOffset UnchangedSince,
    DiscoveryState State);

public enum DiscoveryState
{
    WaitingStable,
    Stable
}
