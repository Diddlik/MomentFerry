using MomentFerry.Core.Domain;
using MomentFerry.Infrastructure.Persistence;

namespace MomentFerry.Tests;

/// <summary>
/// The housekeeping queries, against a real database because what matters about them is exactly what
/// SQLite does: which rows a foreign key protects, and which rows a delete leaves behind.
/// </summary>
public sealed class MaintenanceRepositoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "momentferry-maintenance",
        Guid.NewGuid().ToString("N"));

    private readonly SqliteConnectionFactory _factory;

    public MaintenanceRepositoryTests()
    {
        Directory.CreateDirectory(_directory);
        _factory = new SqliteConnectionFactory(Path.Combine(_directory, "momentferry.db"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    [Fact]
    public async Task ClearMetadataStamp_OnlyTouchesTheRequestedShare()
    {
        await new SqliteDatabaseInitializer(_factory).InitializeAsync();
        var shares = new SqliteShareRepository(_factory);
        var mediaFiles = new SqliteMediaFileRepository(_factory);

        var phone = new Share { Name = "Phone", Path = Path.Combine(_directory, "phone"), Role = ShareRole.Source };
        var camera = new Share { Name = "Camera", Path = Path.Combine(_directory, "camera"), Role = ShareRole.Source };
        await shares.UpsertAsync(phone);
        await shares.UpsertAsync(camera);

        var stamped = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        var onPhone = await AddAsync(mediaFiles, phone.Id, "a.jpg", stamped);
        var onCamera = await AddAsync(mediaFiles, camera.Id, "b.jpg", stamped);

        var affected = await mediaFiles.ClearMetadataStampAsync(phone.Id);

        Assert.Equal(1, affected);
        Assert.Null((await mediaFiles.GetAsync(onPhone))!.SourceLastWriteAt);
        Assert.Equal(stamped, (await mediaFiles.GetAsync(onCamera))!.SourceLastWriteAt);

        Assert.Equal(2, await mediaFiles.ClearMetadataStampAsync(null));
        Assert.Null((await mediaFiles.GetAsync(onCamera))!.SourceLastWriteAt);
    }

    [Fact]
    public async Task DeleteUnreferenced_KeepsRowsAnOperationStillRefersTo()
    {
        await new SqliteDatabaseInitializer(_factory).InitializeAsync();
        var shares = new SqliteShareRepository(_factory);
        var mediaFiles = new SqliteMediaFileRepository(_factory);
        var operations = new SqliteMediaOperationRepository(_factory);

        var phone = new Share { Name = "Phone", Path = Path.Combine(_directory, "phone"), Role = ShareRole.Source };
        await shares.UpsertAsync(phone);

        var stamped = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        var withHistory = await AddAsync(mediaFiles, phone.Id, "routed.jpg", stamped);
        var withoutHistory = await AddAsync(mediaFiles, phone.Id, "never-routed.jpg", stamped);

        await operations.UpsertAsync(new MediaOperation
        {
            Id = Guid.NewGuid(),
            MediaFileId = withHistory,
            State = MediaOperationState.Completed,
            SourcePath = "/shares/routed.jpg",
            StartedAt = stamped,
            CompletedAt = stamped
        });

        var removed = await mediaFiles.DeleteUnreferencedAsync([withHistory, withoutHistory]);

        Assert.Equal(1, removed);
        Assert.NotNull(await mediaFiles.GetAsync(withHistory));
        Assert.Null(await mediaFiles.GetAsync(withoutHistory));
        Assert.Single(await operations.ListRecentAsync());
    }

    [Fact]
    public async Task DeleteFinishedBefore_LeavesAnythingStillWaitingForADecision()
    {
        await new SqliteDatabaseInitializer(_factory).InitializeAsync();
        var shares = new SqliteShareRepository(_factory);
        var mediaFiles = new SqliteMediaFileRepository(_factory);
        var operations = new SqliteMediaOperationRepository(_factory);

        var phone = new Share { Name = "Phone", Path = Path.Combine(_directory, "phone"), Role = ShareRole.Source };
        await shares.UpsertAsync(phone);

        var old = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var recent = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        var media = await AddAsync(mediaFiles, phone.Id, "a.jpg", recent);

        var oldCompleted = await AddOperationAsync(operations, media, MediaOperationState.Completed, old, old);
        var oldQuarantined = await AddOperationAsync(operations, media, MediaOperationState.Quarantined, old, old);
        var oldRetryPending = await AddOperationAsync(operations, media, MediaOperationState.RetryPending, old, old);
        var recentCompleted = await AddOperationAsync(operations, media, MediaOperationState.Completed, recent, recent);

        var removed = await operations.DeleteFinishedBeforeAsync(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(1, removed);
        Assert.Null(await operations.GetAsync(oldCompleted));
        Assert.NotNull(await operations.GetAsync(oldQuarantined));
        Assert.NotNull(await operations.GetAsync(oldRetryPending));
        Assert.NotNull(await operations.GetAsync(recentCompleted));
    }

    private static async Task<Guid> AddAsync(
        SqliteMediaFileRepository repository,
        Guid shareId,
        string name,
        DateTimeOffset stamped)
    {
        var mediaFile = new MediaFile
        {
            Id = Guid.NewGuid(),
            SourceShareId = shareId,
            SourcePath = $"/shares/{shareId:N}/{name}",
            OriginalName = name,
            Size = 1024,
            Extension = Path.GetExtension(name),
            MediaType = MediaType.Image,
            CapturedAt = stamped,
            TimestampSource = "Exif",
            SourceLastWriteAt = stamped,
            FirstSeenAt = stamped,
            LastSeenAt = stamped
        };
        await repository.UpsertAsync(mediaFile);
        return mediaFile.Id;
    }

    private static async Task<Guid> AddOperationAsync(
        SqliteMediaOperationRepository repository,
        Guid mediaFileId,
        MediaOperationState state,
        DateTimeOffset startedAt,
        DateTimeOffset? completedAt)
    {
        var operation = new MediaOperation
        {
            Id = Guid.NewGuid(),
            MediaFileId = mediaFileId,
            State = state,
            SourcePath = "/shares/a.jpg",
            StartedAt = startedAt,
            CompletedAt = completedAt
        };
        await repository.UpsertAsync(operation);
        return operation.Id;
    }
}
