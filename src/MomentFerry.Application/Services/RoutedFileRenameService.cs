using MomentFerry.Application.Abstractions;
using MomentFerry.Core.Domain;

namespace MomentFerry.Application.Services;

/// <summary>
/// Re-applies the current naming rules to the files an event has already stored.
///
/// Renaming happens on the way to the destination, so a rename preset or a camera mapping added
/// afterwards never reaches media that was routed before it existed. <c>Route again</c> cannot repair
/// those names either: it needs a source to re-route, and Safe Move released the sources once their
/// copies were verified. This renames the committed file where it lies.
///
/// Content is never read, written or overwritten: a file is renamed only when the name the current
/// rules produce is free, and the operation history follows the file to its new name so it keeps
/// pointing at the copy it verified.
/// </summary>
public sealed class RoutedFileRenameService(
    IMediaOperationRepository operations,
    IMediaFileRepository mediaFiles,
    IMediaEventRepository events,
    IShareRepository shares,
    RenameContextFactory renameContexts,
    IFileSystemGateway fileSystem)
{
    /// <summary>How many individual decisions are reported back; the counts always cover everything.</summary>
    private const int MaxSamples = 50;

    public async Task<RoutedRenameResult?> RenameAsync(
        Guid eventId,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var mediaEvent = await events.GetAsync(eventId, cancellationToken);
        if (mediaEvent is null) return null;

        var rename = await renameContexts.LoadAsync(cancellationToken);
        var shareById = (await shares.ListAsync(cancellationToken)).ToDictionary(x => x.Id);
        var completed = await operations.ListCompletedByEventAsync(eventId, cancellationToken);

        var renamed = 0;
        var unchanged = 0;
        var skipped = 0;
        var errors = 0;
        var samples = new List<RoutedRenameSample>();
        // A dry run moves nothing, so two files could otherwise be reported as taking the same name.
        var planned = new HashSet<string>(PathComparer);

        foreach (var operation in completed)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(operation.DestinationPath))
            {
                skipped++;
                Sample(samples, operation.SourcePath, null, "No destination was recorded.");
                continue;
            }

            var current = operation.DestinationPath;
            if (!fileSystem.FileExists(current))
            {
                skipped++;
                Sample(samples, current, null, "The stored file is no longer at the destination.");
                continue;
            }

            var mediaFile = await mediaFiles.GetAsync(operation.MediaFileId, cancellationToken);
            if (mediaFile is null ||
                !shareById.TryGetValue(mediaFile.SourceShareId, out var sourceShare) ||
                !shareById.TryGetValue(mediaEvent.DestinationShareId, out var destinationShare))
            {
                skipped++;
                Sample(samples, current, null, "The share or media record behind this file is gone.");
                continue;
            }

            string target;
            try
            {
                // The file's own name must not count as taken, or a numbered template would step every
                // file to the next free sequence on every run.
                var resolver = new DestinationPathResolver(new IgnoringGateway(fileSystem, current));
                target = resolver.Resolve(mediaEvent, sourceShare, destinationShare, mediaFile, rename);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                errors++;
                Sample(samples, current, null, ex.Message);
                continue;
            }

            if (PathComparer.Equals(target, current))
            {
                unchanged++;
                continue;
            }

            if (fileSystem.FileExists(target) || !planned.Add(target))
            {
                skipped++;
                Sample(samples, current, target, "That name is already taken at the destination.");
                continue;
            }

            if (dryRun)
            {
                renamed++;
                Sample(samples, current, target, null);
                continue;
            }

            try
            {
                var lengthBefore = fileSystem.GetFileLength(current);
                var directory = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(directory)) fileSystem.EnsureDirectory(directory);
                fileSystem.MoveFile(current, target);

                if (!fileSystem.FileExists(target) || fileSystem.GetFileLength(target) != lengthBefore)
                {
                    errors++;
                    Sample(samples, current, target, "The renamed file did not arrive intact.");
                    continue;
                }

                await operations.UpsertAsync(WithDestination(operation, target), cancellationToken);
                renamed++;
                Sample(samples, current, target, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors++;
                Sample(samples, current, target, ex.Message);
            }
        }

        return new RoutedRenameResult(
            mediaEvent.Id,
            mediaEvent.Name,
            dryRun,
            completed.Count,
            renamed,
            unchanged,
            skipped,
            errors,
            samples);
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static void Sample(List<RoutedRenameSample> samples, string from, string? to, string? reason)
    {
        if (samples.Count >= MaxSamples) return;
        samples.Add(new RoutedRenameSample(Path.GetFileName(from), to is null ? null : Path.GetFileName(to), reason));
    }

    private static MediaOperation WithDestination(MediaOperation source, string destinationPath) => new()
    {
        Id = source.Id,
        MediaFileId = source.MediaFileId,
        EventId = source.EventId,
        State = source.State,
        SourcePath = source.SourcePath,
        StagingPath = source.StagingPath,
        DestinationPath = destinationPath,
        SourceHash = source.SourceHash,
        DestinationHash = source.DestinationHash,
        RetryCount = source.RetryCount,
        LastError = source.LastError,
        StartedAt = source.StartedAt,
        CompletedAt = source.CompletedAt
    };

    /// <summary>
    /// Hides one path from the sequence probe, so a file is not counted as blocking its own name.
    /// </summary>
    private sealed class IgnoringGateway(IFileSystemGateway inner, string ignored) : IFileSystemGateway
    {
        public bool FileExists(string path) => !PathComparer.Equals(path, ignored) && inner.FileExists(path);
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public IEnumerable<string> EnumerateFiles(string path, bool recursive) => inner.EnumerateFiles(path, recursive);
        public long GetFileLength(string path) => inner.GetFileLength(path);
        public long? GetAvailableFreeSpace(string path) => inner.GetAvailableFreeSpace(path);
        public DateTimeOffset GetLastWriteTimeUtc(string path) => inner.GetLastWriteTimeUtc(path);
        public Stream OpenRead(string path) => inner.OpenRead(path);
        public void MoveFile(string source, string destination) => inner.MoveFile(source, destination);
        public void DeleteFile(string path) => inner.DeleteFile(path);
        public void SetFileTimestampsUtc(string path, DateTimeOffset timestamp) => inner.SetFileTimestampsUtc(path, timestamp);
        public void EnsureDirectory(string path) => inner.EnsureDirectory(path);
        public Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken = default) =>
            inner.CopyFileAsync(source, destination, cancellationToken);
    }
}

public sealed record RoutedRenameResult(
    Guid EventId,
    string EventName,
    bool DryRun,
    int Examined,
    int Renamed,
    int Unchanged,
    int Skipped,
    int Errors,
    IReadOnlyList<RoutedRenameSample> Samples);

/// <summary>One decision, named by filename only: the folder is the event's and never changes here.</summary>
public sealed record RoutedRenameSample(string From, string? To, string? Reason);
