namespace MomentFerry.Application.Abstractions;

public interface IFileSystemGateway
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    IEnumerable<string> EnumerateFiles(string path, bool recursive);
    long GetFileLength(string path);
    DateTimeOffset GetLastWriteTimeUtc(string path);
    long? GetAvailableFreeSpace(string path) => null;
    Stream OpenRead(string path);
    Task CopyFileAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default);
    void MoveFile(string sourcePath, string destinationPath);

    /// <summary>
    /// Stamps a routed file with its capture time. Without it the destination carries the copy time,
    /// which reorders every gallery that sorts by file date instead of by embedded metadata.
    /// </summary>
    void SetFileTimestampsUtc(string path, DateTimeOffset timestamp);
    void DeleteFile(string path);
    void EnsureDirectory(string path);
}
