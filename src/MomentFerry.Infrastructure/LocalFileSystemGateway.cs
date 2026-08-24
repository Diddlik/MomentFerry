using MomentFerry.Application.Abstractions;

namespace MomentFerry.Infrastructure;

public sealed class LocalFileSystemGateway(IRuntimeSettingsStore? settingsStore = null) : IFileSystemGateway
{
    public const long DefaultMinimumFreeSpaceReserveBytes = 512L * 1024L * 1024L;

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IEnumerable<string> EnumerateFiles(string path, bool recursive) =>
        Directory.EnumerateFiles(
            path,
            "*",
            recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);

    public long GetFileLength(string path) => new FileInfo(path).Length;

    public DateTimeOffset GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(path);

    public long? GetAvailableFreeSpace(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            var matchingDrive = DriveInfo.GetDrives()
                .Where(drive => drive.IsReady)
                .Select(drive => new
                {
                    Drive = drive,
                    Root = Path.GetFullPath(drive.RootDirectory.FullName)
                })
                .Where(item => IsWithinRoot(fullPath, item.Root, comparison))
                .OrderByDescending(item => item.Root.Length)
                .FirstOrDefault();

            return matchingDrive?.Drive.AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public Stream OpenRead(string path) => new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 128 * 1024,
        options: FileOptions.Asynchronous | FileOptions.SequentialScan);

    public async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        var sourceLength = new FileInfo(sourcePath).Length;
        var available = GetAvailableFreeSpace(destinationDirectory ?? destinationPath);
        var reserve = settingsStore is null
            ? DefaultMinimumFreeSpaceReserveBytes
            : (await settingsStore.GetAsync(cancellationToken)).MinimumFreeSpaceReserveBytes;
        if (available is long availableBytes)
        {
            var required = sourceLength > long.MaxValue - reserve
                ? long.MaxValue
                : sourceLength + reserve;
            if (availableBytes < required)
            {
                throw new IOException(
                    $"Insufficient destination free space. Required at least {required} bytes " +
                    $"({sourceLength} byte file + {reserve} byte reserve), " +
                    $"but only {availableBytes} bytes are available.");
            }
        }

        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 128 * 1024,
            options: FileOptions.Asynchronous | FileOptions.WriteThrough);

        await source.CopyToAsync(destination, cancellationToken);
        await destination.FlushAsync(cancellationToken);
    }

    public void MoveFile(string sourcePath, string destinationPath)
    {
        var destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        File.Move(sourcePath, destinationPath, overwrite: false);
    }

    public void SetFileTimestampsUtc(string path, DateTimeOffset timestamp)
    {
        var utc = timestamp.UtcDateTime;
        File.SetLastWriteTimeUtc(path, utc);

        // Linux offers no way to set a file's birth time, so the Docker deployment carries the capture
        // time in the last-write time only. Windows development runs stamp both.
        if (OperatingSystem.IsWindows()) File.SetCreationTimeUtc(path, utc);
    }

    public void DeleteFile(string path) => File.Delete(path);

    public void EnsureDirectory(string path) => Directory.CreateDirectory(path);

    private static bool IsWithinRoot(string path, string root, StringComparison comparison)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (normalizedRoot.Length == 0)
        {
            normalizedRoot = Path.DirectorySeparatorChar.ToString();
        }

        if (string.Equals(path, normalizedRoot, comparison)) return true;
        var prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, comparison);
    }
}
