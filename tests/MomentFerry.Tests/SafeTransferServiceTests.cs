using System.Security.Cryptography;
using MomentFerry.Application.Abstractions;
using MomentFerry.Application.Services;
using MomentFerry.Core.Domain;
using MomentFerry.Infrastructure;
using MomentFerry.Infrastructure.Persistence;

namespace MomentFerry.Tests;

public sealed class SafeTransferServiceTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "momentferry-safe-transfer", Guid.NewGuid().ToString("N"));
    private string SourceDirectory => Path.Combine(_root, "source");
    private string DestinationDirectory => Path.Combine(_root, "destination");
    private string DatabasePath => Path.Combine(_root, "momentferry.db");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(SourceDirectory);
        Directory.CreateDirectory(DestinationDirectory);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task SafeMove_DeletesSourceOnlyAfterCommittedDestinationWasPersistedAndVerified()
    {
        var fixture = await CreateFixtureAsync();
        var deletionObserved = false;
        var invariantHeldAtDelete = false;

        fixture.FileSystem.BeforeDelete = path =>
        {
            if (!PathEquals(path, fixture.Media.SourcePath)) return;
            deletionObserved = true;
            var state = fixture.Operations.LastPersisted?.State;
            var destination = fixture.Operations.LastPersisted?.DestinationPath;
            invariantHeldAtDelete =
                state == MediaOperationState.SourceFinalizePending &&
                !string.IsNullOrWhiteSpace(destination) &&
                File.Exists(destination) &&
                File.ReadAllBytes(destination).SequenceEqual(fixture.SourceBytes);
        };

        var result = await fixture.Service.ExecuteAsync(fixture.Media.Id, fixture.Event.Id);

        Assert.Equal(MediaOperationState.Completed, result.Operation.State);
        Assert.True(result.SourceDeleted);
        Assert.True(result.DestinationCreated);
        Assert.True(deletionObserved);
        Assert.True(invariantHeldAtDelete);
        Assert.False(File.Exists(fixture.Media.SourcePath));
        Assert.NotNull(result.Operation.DestinationPath);
        Assert.True(File.Exists(result.Operation.DestinationPath!));
        Assert.Equal(fixture.SourceBytes, File.ReadAllBytes(result.Operation.DestinationPath!));
    }

    [Fact]
    public async Task SafeMove_StampsTheDestinationWithTheCaptureTime()
    {
        var fixture = await CreateFixtureAsync();

        var result = await fixture.Service.ExecuteAsync(fixture.Media.Id, fixture.Event.Id);

        Assert.Equal(MediaOperationState.Completed, result.Operation.State);
        Assert.Null(result.Message);
        Assert.Equal(
            fixture.Media.CapturedAt!.Value.UtcDateTime,
            File.GetLastWriteTimeUtc(result.Operation.DestinationPath!));
    }

    [Fact]
    public async Task SafeMove_WhenStagingHashDiffers_QuarantinesAndPreservesSource()
    {
        var fixture = await CreateFixtureAsync();
        fixture.FileSystem.AfterCopy = (_, destination) =>
        {
            var corrupted = fixture.SourceBytes.ToArray();
            corrupted[0] ^= 0xff;
            File.WriteAllBytes(destination, corrupted);
        };

        var result = await fixture.Service.ExecuteAsync(fixture.Media.Id, fixture.Event.Id);

        Assert.Equal(MediaOperationState.Quarantined, result.Operation.State);
        Assert.False(result.SourceDeleted);
        Assert.False(result.DestinationCreated);
        Assert.True(File.Exists(fixture.Media.SourcePath));
        Assert.Equal(0, fixture.FileSystem.SourceDeleteCount);
    }

    [Fact]
    public async Task SafeMove_WhenFinalCommitPersistenceFails_PreservesSource()
    {
        var fixture = await CreateFixtureAsync();
        fixture.Operations.FailOnceOnState = MediaOperationState.DestinationCommitted;

        await Assert.ThrowsAsync<IOException>(() =>
            fixture.Service.ExecuteAsync(fixture.Media.Id, fixture.Event.Id));

        Assert.True(File.Exists(fixture.Media.SourcePath));
        Assert.Equal(0, fixture.FileSystem.SourceDeleteCount);
        Assert.NotNull(fixture.Operations.LastPersisted);
        Assert.Equal(MediaOperationState.SourceFinalizePending, fixture.Operations.LastPersisted!.State);
        Assert.NotNull(fixture.Operations.LastPersisted.DestinationPath);
        Assert.True(File.Exists(fixture.Operations.LastPersisted.DestinationPath!));
    }

    [Fact]
    public async Task SafeMove_WhenSourceDeleteFails_KeepsRecoverableCommittedState()
    {
        var fixture = await CreateFixtureAsync();
        fixture.FileSystem.ThrowOnSourceDelete = true;

        await Assert.ThrowsAsync<IOException>(() =>
            fixture.Service.ExecuteAsync(fixture.Media.Id, fixture.Event.Id));

        Assert.True(File.Exists(fixture.Media.SourcePath));
        Assert.NotNull(fixture.Operations.LastPersisted);
        Assert.Equal(MediaOperationState.SourceFinalizePending, fixture.Operations.LastPersisted!.State);
        Assert.NotNull(fixture.Operations.LastPersisted.DestinationPath);
        Assert.True(File.Exists(fixture.Operations.LastPersisted.DestinationPath!));
        Assert.Equal(fixture.SourceBytes, File.ReadAllBytes(fixture.Operations.LastPersisted.DestinationPath!));
    }

    [Fact]
    public async Task SafeMove_WhenItsOwnOutputReturnsToTheSource_HoldsItInsteadOfDeletingIt()
    {
        var fixture = await CreateFixtureAsync();
        var first = await fixture.Service.ExecuteAsync(fixture.Media.Id, fixture.Event.Id);
        Assert.Equal(MediaOperationState.Completed, first.Operation.State);

        // Something mirrors the destination back into the source share, under the name MomentFerry
        // gave the file. It arrives as a new media file with identical content.
        var mirrorDirectory = Path.Combine(SourceDirectory, "mirrored");
        Directory.CreateDirectory(mirrorDirectory);
        var returnedPath = Path.Combine(mirrorDirectory, Path.GetFileName(first.Operation.DestinationPath!));
        await File.WriteAllBytesAsync(returnedPath, fixture.SourceBytes);
        var returned = new MediaFile
        {
            Id = Guid.NewGuid(),
            SourceShareId = fixture.Media.SourceShareId,
            SourcePath = returnedPath,
            OriginalName = Path.GetFileName(returnedPath),
            Size = fixture.SourceBytes.Length,
            Extension = Path.GetExtension(returnedPath),
            MediaType = MediaType.Image,
            CapturedAt = fixture.Media.CapturedAt,
            TimestampSource = fixture.Media.TimestampSource,
            FirstSeenAt = fixture.Media.FirstSeenAt,
            LastSeenAt = fixture.Media.LastSeenAt
        };
        await fixture.MediaFiles.UpsertAsync(returned);

        var second = await fixture.Service.ExecuteAsync(returned.Id, fixture.Event.Id);

        Assert.Equal(MediaOperationState.Quarantined, second.Operation.State);
        Assert.False(second.SourceDeleted);
        Assert.Contains("own output", second.Operation.LastError);
        Assert.True(File.Exists(returnedPath));
        Assert.True(File.Exists(first.Operation.DestinationPath!));
    }

    [Fact]
    public async Task RouteAgain_AfterTheSourceReturned_RoutesItUnderTheCurrentRulesAndKeepsTheOldCopy()
    {
        var fixture = await CreateFixtureAsync();
        var first = await fixture.Service.ExecuteAsync(fixture.Media.Id, fixture.Event.Id);
        Assert.Equal(MediaOperationState.Completed, first.Operation.State);
        Assert.False(File.Exists(fixture.Media.SourcePath));

        // The user puts the file back and asks for it to be routed again, for example after changing
        // the naming rules. The coordinator would otherwise refuse: the pair already reached a
        // terminal state.
        await File.WriteAllBytesAsync(fixture.Media.SourcePath, fixture.SourceBytes);
        var blocked = await fixture.Coordinator.ExecuteOnceAsync(fixture.Media.Id, fixture.Event.Id);
        Assert.False(blocked.Executed);

        var again = await fixture.Retry.RouteAgainAsync(first.Operation.Id);

        Assert.Equal(MediaOperationState.Completed, again.Operation.State);
        Assert.NotEqual(first.Operation.Id, again.Operation.Id);
        Assert.True(File.Exists(again.Operation.DestinationPath!));

        var superseded = Assert.Single(
            await fixture.Operations.ListByStateAsync(MediaOperationState.Failed),
            x => x.Id == first.Operation.Id);
        Assert.Equal("Superseded by an explicit route-again request.", superseded.LastError);
    }

    [Fact]
    public async Task SupersedeTerminalByEvent_UnblocksTheWholeEventAndLeavesHeldOperationsAlone()
    {
        var fixture = await CreateFixtureAsync();
        var first = await fixture.Service.ExecuteAsync(fixture.Media.Id, fixture.Event.Id);
        Assert.Equal(MediaOperationState.Completed, first.Operation.State);
        await File.WriteAllBytesAsync(fixture.Media.SourcePath, fixture.SourceBytes);

        var otherMedia = new MediaFile
        {
            Id = Guid.NewGuid(),
            SourceShareId = fixture.Media.SourceShareId,
            SourcePath = Path.Combine(SourceDirectory, "held.jpg"),
            OriginalName = "held.jpg",
            Size = 1,
            Extension = ".jpg",
            MediaType = MediaType.Image,
            CapturedAt = fixture.Media.CapturedAt,
            TimestampSource = fixture.Media.TimestampSource,
            FirstSeenAt = fixture.Media.FirstSeenAt,
            LastSeenAt = fixture.Media.LastSeenAt
        };
        await fixture.MediaFiles.UpsertAsync(otherMedia);

        var held = new MediaOperation
        {
            Id = Guid.NewGuid(),
            MediaFileId = otherMedia.Id,
            EventId = fixture.Event.Id,
            State = MediaOperationState.Quarantined,
            SourcePath = Path.Combine(SourceDirectory, "held.jpg"),
            StartedAt = fixture.Event.StartAt
        };
        await fixture.Operations.UpsertAsync(held);

        var superseded = await fixture.Operations.SupersedeTerminalByEventAsync(
            fixture.Event.Id,
            "Superseded by an explicit route-again request for the whole event.",
            fixture.Event.StartAt.AddDays(2));

        Assert.Equal(1, superseded);
        Assert.Equal(
            MediaOperationState.Quarantined,
            (await fixture.Operations.GetAsync(held.Id))!.State);

        var again = await fixture.Coordinator.ExecuteOnceAsync(fixture.Media.Id, fixture.Event.Id);
        Assert.True(again.Executed);
        Assert.Equal(MediaOperationState.Completed, again.Result!.Operation.State);
    }

    [Fact]
    public async Task Coordinator_ConcurrentCalls_ExecuteExactlyOneTransfer()
    {
        var fixture = await CreateFixtureAsync();
        fixture.FileSystem.CopyDelay = TimeSpan.FromMilliseconds(150);

        var first = fixture.Coordinator.ExecuteOnceAsync(fixture.Media.Id, fixture.Event.Id);
        var second = fixture.Coordinator.ExecuteOnceAsync(fixture.Media.Id, fixture.Event.Id);
        var results = await Task.WhenAll(first, second);

        Assert.Single(results, x => x.Executed);
        Assert.Single(results, x => !x.Executed);
        Assert.False(File.Exists(fixture.Media.SourcePath));
        var persisted = await fixture.Operations.ListRecentAsync();
        var operation = Assert.Single(persisted, x => x.MediaFileId == fixture.Media.Id);
        Assert.Equal(MediaOperationState.Completed, operation.State);
    }

    private async Task<Fixture> CreateFixtureAsync()
    {
        var sourceShare = new Share
        {
            Id = Guid.NewGuid(),
            Name = "Phone",
            Path = SourceDirectory,
            Role = ShareRole.Source,
            Enabled = true
        };
        var destinationShare = new Share
        {
            Id = Guid.NewGuid(),
            Name = "Family",
            Path = DestinationDirectory,
            Role = ShareRole.Destination,
            Enabled = true
        };
        var sourceGroup = new SourceGroup
        {
            Id = Guid.NewGuid(),
            Name = "Family phones",
            ShareIds = [sourceShare.Id]
        };
        var capturedAt = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        var mediaEvent = new MediaEvent
        {
            Id = Guid.NewGuid(),
            Name = "Vacation 2026",
            StartAt = capturedAt.AddDays(-1),
            EndAt = capturedAt.AddDays(1),
            Status = MediaEventStatus.Closed,
            SourceGroupId = sourceGroup.Id,
            DestinationShareId = destinationShare.Id,
            DestinationFolderTemplate = "{event.name}",
            OperationMode = OperationMode.SafeMove,
            ConflictStrategy = ConflictStrategy.AppendSourceName,
            DuplicateStrategy = DuplicateStrategy.SafeMoveToExisting
        };

        var sourceBytes = Enumerable.Range(0, 32 * 1024).Select(i => (byte)(i % 251)).ToArray();
        var sourcePath = Path.Combine(SourceDirectory, "IMG_0001.jpg");
        await File.WriteAllBytesAsync(sourcePath, sourceBytes);
        var media = new MediaFile
        {
            Id = Guid.NewGuid(),
            SourceShareId = sourceShare.Id,
            SourcePath = sourcePath,
            OriginalName = "IMG_0001.jpg",
            Size = sourceBytes.Length,
            Extension = ".jpg",
            MediaType = MediaType.Image,
            CapturedAt = capturedAt,
            TimestampSource = "DateTimeOriginal",
            FirstSeenAt = capturedAt,
            LastSeenAt = capturedAt
        };

        var connectionFactory = new SqliteConnectionFactory(DatabasePath);
        await new SqliteDatabaseInitializer(connectionFactory).InitializeAsync();
        var shares = new SqliteShareRepository(connectionFactory);
        var groups = new SqliteSourceGroupRepository(connectionFactory);
        var events = new SqliteMediaEventRepository(connectionFactory);
        var mediaFiles = new SqliteMediaFileRepository(connectionFactory);
        var innerOperations = new SqliteMediaOperationRepository(connectionFactory);
        var operations = new TrackingOperationRepository(innerOperations);

        await shares.UpsertAsync(sourceShare);
        await shares.UpsertAsync(destinationShare);
        await groups.UpsertAsync(sourceGroup);
        await events.UpsertAsync(mediaEvent);
        await mediaFiles.UpsertAsync(media);

        var fileSystem = new TrackingFileSystemGateway(new LocalFileSystemGateway(), sourcePath);
        var service = new SafeTransferService(
            mediaFiles,
            operations,
            events,
            groups,
            shares,
            fileSystem,
            new TestHashService(),
            new DestinationPathResolver(fileSystem),
            new RenameContextFactory(
                new SqliteRenamePresetRepository(connectionFactory),
                new SqliteCameraMappingRepository(connectionFactory)),
            new FixedClock(capturedAt.AddHours(1)));
        var coordinator = new TransferCoordinator(operations, service);

        var retry = new OperationRetryService(operations, events, shares, fileSystem, service, new FixedClock(capturedAt.AddHours(2)));

        return new Fixture(service, coordinator, retry, operations, mediaFiles, fileSystem, media, mediaEvent, sourceBytes);
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private sealed record Fixture(
        SafeTransferService Service,
        TransferCoordinator Coordinator,
        OperationRetryService Retry,
        TrackingOperationRepository Operations,
        IMediaFileRepository MediaFiles,
        TrackingFileSystemGateway FileSystem,
        MediaFile Media,
        MediaEvent Event,
        byte[] SourceBytes);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class TestHashService : IHashService
    {
        public async Task<string> ComputeSha256Async(Stream stream, CancellationToken cancellationToken = default)
        {
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }

    private sealed class TrackingFileSystemGateway(IFileSystemGateway inner, string sourcePath) : IFileSystemGateway
    {
        public Action<string>? BeforeDelete { get; set; }
        public Action<string, string>? AfterCopy { get; set; }
        public bool ThrowOnSourceDelete { get; set; }
        public TimeSpan CopyDelay { get; set; }
        public int SourceDeleteCount { get; private set; }

        public bool FileExists(string path) => inner.FileExists(path);
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public IEnumerable<string> EnumerateFiles(string path, bool recursive) => inner.EnumerateFiles(path, recursive);
        public long GetFileLength(string path) => inner.GetFileLength(path);
        public DateTimeOffset GetLastWriteTimeUtc(string path) => inner.GetLastWriteTimeUtc(path);
        public Stream OpenRead(string path) => inner.OpenRead(path);
        public void MoveFile(string source, string destination) => inner.MoveFile(source, destination);
        public void SetFileTimestampsUtc(string path, DateTimeOffset timestamp) => inner.SetFileTimestampsUtc(path, timestamp);
        public void EnsureDirectory(string path) => inner.EnsureDirectory(path);

        public async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken = default)
        {
            if (CopyDelay > TimeSpan.Zero) await Task.Delay(CopyDelay, cancellationToken);
            await inner.CopyFileAsync(source, destination, cancellationToken);
            AfterCopy?.Invoke(source, destination);
        }

        public void DeleteFile(string path)
        {
            BeforeDelete?.Invoke(path);
            if (PathEquals(path, sourcePath))
            {
                SourceDeleteCount++;
                if (ThrowOnSourceDelete) throw new IOException("Simulated source delete failure.");
            }
            inner.DeleteFile(path);
        }
    }

    private sealed class TrackingOperationRepository(IMediaOperationRepository inner) : IMediaOperationRepository
    {
        public MediaOperation? LastPersisted { get; private set; }
        public MediaOperationState? FailOnceOnState { get; set; }
        private bool _failureConsumed;

        public Task<IReadOnlyList<MediaOperation>> ListRecentAsync(int limit = 200, CancellationToken cancellationToken = default) =>
            inner.ListRecentAsync(limit, cancellationToken);
        public Task<IReadOnlyList<MediaOperation>> ListByStateAsync(MediaOperationState state, int limit = 200, CancellationToken cancellationToken = default) =>
            inner.ListByStateAsync(state, limit, cancellationToken);
        public Task<IReadOnlyDictionary<MediaOperationState, long>> CountByStateAsync(CancellationToken cancellationToken = default) =>
            inner.CountByStateAsync(cancellationToken);
        public Task<IReadOnlyList<MediaOperation>> ListIncompleteAsync(CancellationToken cancellationToken = default) =>
            inner.ListIncompleteAsync(cancellationToken);
        public Task<MediaOperation?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            inner.GetAsync(id, cancellationToken);
        public Task<MediaOperation?> GetIncompleteByMediaFileAsync(Guid mediaFileId, CancellationToken cancellationToken = default) =>
            inner.GetIncompleteByMediaFileAsync(mediaFileId, cancellationToken);
        public Task<MediaOperation?> FindCompletedByDestinationHashAsync(string destinationHash, Guid excludedMediaFileId, CancellationToken cancellationToken = default) =>
            inner.FindCompletedByDestinationHashAsync(destinationHash, excludedMediaFileId, cancellationToken);
        public Task<int> SupersedeTerminalByEventAsync(Guid eventId, string reason, DateTimeOffset supersededAt, CancellationToken cancellationToken = default) =>
            inner.SupersedeTerminalByEventAsync(eventId, reason, supersededAt, cancellationToken);
        public Task<bool> HasTerminalOperationAsync(Guid mediaFileId, Guid eventId, CancellationToken cancellationToken = default) =>
            inner.HasTerminalOperationAsync(mediaFileId, eventId, cancellationToken);

        public Task<int> DeleteFinishedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default) =>
            inner.DeleteFinishedBeforeAsync(cutoff, cancellationToken);

        public async Task UpsertAsync(MediaOperation operation, CancellationToken cancellationToken = default)
        {
            if (!_failureConsumed && FailOnceOnState == operation.State)
            {
                _failureConsumed = true;
                throw new IOException($"Simulated persistence failure for {operation.State}.");
            }

            await inner.UpsertAsync(operation, cancellationToken);
            LastPersisted = operation;
        }
    }
}
