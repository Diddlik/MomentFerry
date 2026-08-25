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
/// A file is renamed only when the name the current rules produce is free, and only when the bytes on
/// disk still match the checksum its operation recorded. That check is the point: the operation is the
/// record that this content was verified, and moving it to a new name without proving the content is
/// still the content would let a replaced or damaged file inherit a verification it never earned.
/// Nothing is ever overwritten, and the history follows the file so it keeps pointing at its copy.
/// </summary>
public sealed class RoutedFileRenameService(
    IMediaOperationRepository operations,
    IMediaFileRepository mediaFiles,
    IMediaEventRepository events,
    IShareRepository shares,
    RenameContextFactory renameContexts,
    IFileSystemGateway fileSystem,
    IHashService hashes)
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
        // Sampling is capped, so the reasons are tallied separately: a run that skips a thousand files
        // must say why without making the caller infer it from the first fifty.
        var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
        // A dry run moves nothing, so two files could otherwise be reported as taking the same name.
        var planned = new HashSet<string>(PathComparer);

        foreach (var operation in completed)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(operation.DestinationPath))
            {
                skipped++;
                Sample(samples, reasons, operation.SourcePath, null, "No destination was recorded.");
                continue;
            }

            var current = operation.DestinationPath;
            if (!fileSystem.FileExists(current))
            {
                skipped++;
                Sample(samples, reasons, current, null, "The stored file is no longer at the destination.");
                continue;
            }

            var mediaFile = await mediaFiles.GetAsync(operation.MediaFileId, cancellationToken);
            if (mediaFile is null ||
                !shareById.TryGetValue(mediaFile.SourceShareId, out var sourceShare) ||
                !shareById.TryGetValue(mediaEvent.DestinationShareId, out var destinationShare))
            {
                skipped++;
                Sample(samples, reasons, current, null, "The share or media record behind this file is gone.");
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
                Sample(samples, reasons, current, null, ex.Message);
                continue;
            }

            if (PathComparer.Equals(target, current))
            {
                unchanged++;
                continue;
            }

            if (fileSystem.FileExists(target))
            {
                skipped++;
                Sample(samples, reasons, current, target, "That name is already taken at the destination.");
                continue;
            }

            // The operation is the record that this content was verified. A rename that carried that
            // record over to a new name without re-proving the content would hand a replaced or
            // damaged file a verification it never earned, so the bytes decide here too. Only files
            // that are actually about to move are read: a library whose names are already correct
            // costs nothing on the next run.
            if (string.IsNullOrWhiteSpace(operation.DestinationHash))
            {
                skipped++;
                Sample(samples, reasons, current, target, "No checksum was recorded for the stored file.");
                continue;
            }

            string storedHash;
            try
            {
                await using var stream = fileSystem.OpenRead(current);
                storedHash = await hashes.ComputeSha256Async(stream, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors++;
                Sample(samples, reasons, current, target, ex.Message);
                continue;
            }

            if (!string.Equals(storedHash, operation.DestinationHash, StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                Sample(samples, reasons, current, target, "The stored file no longer matches the checksum on record.");
                continue;
            }

            // Reserved only once the file has earned the name: a rejected file that reserved its
            // target would push the file that legitimately renders to it aside as well.
            if (!planned.Add(target))
            {
                skipped++;
                Sample(samples, reasons, current, target, "That name is already taken at the destination.");
                continue;
            }

            if (dryRun)
            {
                renamed++;
                Sample(samples, reasons, current, target, null);
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
                    Sample(samples, reasons, current, target, "The renamed file did not arrive intact.");
                    continue;
                }

                await operations.UpsertAsync(WithDestination(operation, target), cancellationToken);
                renamed++;
                Sample(samples, reasons, current, target, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors++;
                Sample(samples, reasons, current, target, ex.Message);
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
            samples,
            reasons);
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static void Sample(
        List<RoutedRenameSample> samples,
        Dictionary<string, int> reasons,
        string from,
        string? to,
        string? reason)
    {
        if (reason is not null) reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
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
    IReadOnlyList<RoutedRenameSample> Samples,
    /// <summary>Every reason a file was skipped or failed, with how often it applied.</summary>
    IReadOnlyDictionary<string, int> Reasons);

/// <summary>One decision, named by filename only: the folder is the event's and never changes here.</summary>
public sealed record RoutedRenameSample(string From, string? To, string? Reason);
