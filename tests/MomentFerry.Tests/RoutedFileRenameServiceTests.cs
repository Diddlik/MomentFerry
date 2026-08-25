using MomentFerry.Application.Abstractions;
using MomentFerry.Application.Services;
using MomentFerry.Core.Domain;
using MomentFerry.Infrastructure;
using MomentFerry.Infrastructure.Persistence;

namespace MomentFerry.Tests;

/// <summary>
/// Renaming files an event already stored, against a real database and a real filesystem: what matters
/// is what happens to the file on disk and to the operation that vouches for it.
/// </summary>
public sealed class RoutedFileRenameServiceTests : IDisposable
{
    private const string StoredContent = "verified content";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "momentferry-rename-routed",
        Guid.NewGuid().ToString("N"));

    private readonly SqliteConnectionFactory _factory;

    public RoutedFileRenameServiceTests()
    {
        Directory.CreateDirectory(_directory);
        _factory = new SqliteConnectionFactory(Path.Combine(_directory, "momentferry.db"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    [Fact]
    public async Task Rename_AppliesACameraMappingAddedAfterTheFileWasRouted()
    {
        var fixture = await CreateAsync();
        var stored = await StoreAsync(fixture, "20260814_174744_OnePlus 12.jpg");

        // The mapping the user adds after seeing the raw model in the stored name.
        await fixture.CameraMappings.UpsertAsync(new CameraMapping { From = "OnePlus 12", To = "OnePlus12" });

        var result = await fixture.Service.RenameAsync(fixture.Event.Id, dryRun: false);

        Assert.NotNull(result);
        Assert.Equal(1, result!.Examined);
        Assert.Equal(1, result.Renamed);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(0, result.Errors);

        var renamed = Path.Combine(Path.GetDirectoryName(stored.DestinationPath!)!, "20260814_174744_OnePlus12.jpg");
        Assert.True(File.Exists(renamed));
        Assert.False(File.Exists(stored.DestinationPath!));
        Assert.Equal(StoredContent, await File.ReadAllTextAsync(renamed));

        // The history must follow the file, or it would vouch for a path that no longer exists.
        var persisted = await fixture.Operations.GetAsync(stored.Id);
        Assert.Equal(renamed, persisted!.DestinationPath);
        Assert.Equal(MediaOperationState.Completed, persisted.State);
        Assert.Equal(stored.DestinationHash, persisted.DestinationHash);
    }

    [Fact]
    public async Task Rename_RefusesToMoveAStoredFileThatNoLongerMatchesItsChecksum()
    {
        var fixture = await CreateAsync();
        var stored = await StoreAsync(fixture, "20260814_174744_OnePlus 12.jpg");
        await fixture.CameraMappings.UpsertAsync(new CameraMapping { From = "OnePlus 12", To = "OnePlus12" });

        // Something replaced the stored file outside MomentFerry. Renaming it would carry the
        // operation's verification over to content that was never verified.
        await File.WriteAllTextAsync(stored.DestinationPath!, "replaced content");

        var result = await fixture.Service.RenameAsync(fixture.Event.Id, dryRun: false);

        Assert.Equal(1, result!.Skipped);
        Assert.Equal(0, result.Renamed);
        Assert.True(File.Exists(stored.DestinationPath!));
        var sample = Assert.Single(result.Samples);
        Assert.Contains("checksum", sample.Reason);
        Assert.Equal(stored.DestinationPath, (await fixture.Operations.GetAsync(stored.Id))!.DestinationPath);
    }

    [Fact]
    public async Task Rename_SkipsAnOperationWithNoRecordedChecksum()
    {
        var fixture = await CreateAsync();
        var stored = await StoreAsync(fixture, "20260814_174744_OnePlus 12.jpg", withHash: false);
        await fixture.CameraMappings.UpsertAsync(new CameraMapping { From = "OnePlus 12", To = "OnePlus12" });

        var result = await fixture.Service.RenameAsync(fixture.Event.Id, dryRun: false);

        Assert.Equal(1, result!.Skipped);
        Assert.Equal(0, result.Renamed);
        Assert.True(File.Exists(stored.DestinationPath!));
    }

    [Fact]
    public async Task Rename_ReadsTheCaptureOffsetOffTheStoredCopyWhenTheIndexHasNone()
    {
        // A row indexed before the offset was persisted: its source is long gone, so the only place
        // left to read the offset is the stored copy itself. The share's zone is UTC here, so a name
        // in +02:00 can only have come from the file: 17:47:44Z was written as 19:47:44.
        var fixture = await CreateAsync(storedFileOffsetMinutes: 120);
        await fixture.CameraMappings.UpsertAsync(new CameraMapping { From = "OnePlus 12", To = "OnePlus12" });
        var stored = await StoreAsync(fixture, "20260814_174744_OnePlus12.jpg", offsetMinutes: null);

        var result = await fixture.Service.RenameAsync(fixture.Event.Id, dryRun: false);

        Assert.Equal(1, result!.Renamed);
        var renamed = Path.Combine(Path.GetDirectoryName(stored.DestinationPath!)!, "20260814_194744_OnePlus12.jpg");
        Assert.True(File.Exists(renamed));

        // Persisted, so the next run neither re-reads it nor falls back to the share's zone.
        var persisted = await fixture.MediaFiles.GetAsync(fixture.Media.Id);
        Assert.Equal(120, persisted!.CapturedAtOffsetMinutes);
    }

    [Fact]
    public async Task Rename_DoesNotRecordAnOffsetTheStoredFileNeverStated()
    {
        // The extractor can only infer the share's zone here. Recording that would dress an
        // assumption up as evidence, so the row keeps its null and the fallback stays a fallback.
        var fixture = await CreateAsync(storedFileOffsetMinutes: null);
        await StoreAsync(fixture, "20260814_174744.jpg", offsetMinutes: null);

        await fixture.Service.RenameAsync(fixture.Event.Id, dryRun: false);

        var persisted = await fixture.MediaFiles.GetAsync(fixture.Media.Id);
        Assert.Null(persisted!.CapturedAtOffsetMinutes);
    }

    [Fact]
    public async Task Rename_TalliesEveryReasonEvenBeyondTheSampleCap()
    {
        var fixture = await CreateAsync();
        var moved = await StoreAsync(fixture, "20260814_174744_OnePlus 12.jpg");
        // A second file, one second later, so the two never compete for the same target name and the
        // outcome cannot depend on the order the operations come back in.
        var second = await AddMediaAsync(fixture, seconds: 45);
        var replaced = await StoreAsync(fixture, "20260814_174745_OnePlus 12.jpg", media: second);
        await File.WriteAllTextAsync(replaced.DestinationPath!, "replaced content");
        await fixture.CameraMappings.UpsertAsync(new CameraMapping { From = "OnePlus 12", To = "OnePlus12" });

        var result = await fixture.Service.RenameAsync(fixture.Event.Id, dryRun: false);

        Assert.Equal(2, result!.Examined);
        Assert.Equal(1, result.Renamed);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(
            new Dictionary<string, int> { ["The stored file no longer matches the checksum on record."] = 1 },
            result.Reasons);
        Assert.True(File.Exists(Path.Combine(
            Path.GetDirectoryName(moved.DestinationPath!)!,
            "20260814_174744_OnePlus12.jpg")));
        Assert.True(File.Exists(replaced.DestinationPath!));
    }

    [Fact]
    public async Task Rename_LeavesAFileWhoseNameAlreadyMatchesTheRulesAlone()
    {
        var fixture = await CreateAsync();
        await fixture.CameraMappings.UpsertAsync(new CameraMapping { From = "OnePlus 12", To = "OnePlus12" });
        var stored = await StoreAsync(fixture, "20260814_174744_OnePlus12.jpg");

        var result = await fixture.Service.RenameAsync(fixture.Event.Id, dryRun: false);

        Assert.Equal(1, result!.Unchanged);
        Assert.Equal(0, result.Renamed);
        Assert.True(File.Exists(stored.DestinationPath!));
    }

    [Fact]
    public async Task Rename_NeverOverwritesAFileThatAlreadyHoldsTheTargetName()
    {
        var fixture = await CreateAsync();
        var stored = await StoreAsync(fixture, "20260814_174744_OnePlus 12.jpg");
        await fixture.CameraMappings.UpsertAsync(new CameraMapping { From = "OnePlus 12", To = "OnePlus12" });

        var occupied = Path.Combine(Path.GetDirectoryName(stored.DestinationPath!)!, "20260814_174744_OnePlus12.jpg");
        await File.WriteAllTextAsync(occupied, "someone else");

        var result = await fixture.Service.RenameAsync(fixture.Event.Id, dryRun: false);

        Assert.Equal(1, result!.Skipped);
        Assert.Equal(0, result.Renamed);
        Assert.True(File.Exists(stored.DestinationPath!));
        Assert.Equal("someone else", await File.ReadAllTextAsync(occupied));
    }

    [Fact]
    public async Task Rename_InDryRunReportsThePlanAndTouchesNothing()
    {
        var fixture = await CreateAsync();
        var stored = await StoreAsync(fixture, "20260814_174744_OnePlus 12.jpg");
        await fixture.CameraMappings.UpsertAsync(new CameraMapping { From = "OnePlus 12", To = "OnePlus12" });

        var result = await fixture.Service.RenameAsync(fixture.Event.Id, dryRun: true);

        Assert.True(result!.DryRun);
        Assert.Equal(1, result.Renamed);
        var sample = Assert.Single(result.Samples);
        Assert.Equal("20260814_174744_OnePlus 12.jpg", sample.From);
        Assert.Equal("20260814_174744_OnePlus12.jpg", sample.To);
        Assert.Null(sample.Reason);
        Assert.True(File.Exists(stored.DestinationPath!));
        Assert.Equal(stored.DestinationPath, (await fixture.Operations.GetAsync(stored.Id))!.DestinationPath);
    }

    [Fact]
    public async Task Rename_InDryRunReportsAChecksumMismatchInsteadOfPromisingTheRename()
    {
        var fixture = await CreateAsync();
        var stored = await StoreAsync(fixture, "20260814_174744_OnePlus 12.jpg");
        await fixture.CameraMappings.UpsertAsync(new CameraMapping { From = "OnePlus 12", To = "OnePlus12" });
        await File.WriteAllTextAsync(stored.DestinationPath!, "replaced content");

        var result = await fixture.Service.RenameAsync(fixture.Event.Id, dryRun: true);

        Assert.Equal(0, result!.Renamed);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public async Task Rename_SkipsAnOperationWhoseStoredFileIsGone()
    {
        var fixture = await CreateAsync();
        var stored = await StoreAsync(fixture, "20260814_174744_OnePlus 12.jpg");
        await fixture.CameraMappings.UpsertAsync(new CameraMapping { From = "OnePlus 12", To = "OnePlus12" });
        File.Delete(stored.DestinationPath!);

        var result = await fixture.Service.RenameAsync(fixture.Event.Id, dryRun: false);

        Assert.Equal(1, result!.Skipped);
        Assert.Equal(0, result.Renamed);
        Assert.Equal(0, result.Errors);
    }

    [Fact]
    public async Task Rename_KeepsANumberedNameOnItsOwnSequenceInsteadOfStepping()
    {
        var fixture = await CreateAsync();
        await fixture.Presets.UpsertAsync(new RenamePreset
        {
            Id = fixture.PresetId,
            Name = "numbered",
            Template = "{captured:yyyyMMdd_HHmmss}_{camera}_{seq:0000}"
        });
        await fixture.CameraMappings.UpsertAsync(new CameraMapping { From = "OnePlus 12", To = "OnePlus12" });
        var stored = await StoreAsync(fixture, "20260814_174744_OnePlus12_0001.jpg");

        var result = await fixture.Service.RenameAsync(fixture.Event.Id, dryRun: false);

        // Its own name must not count as taken, or every run would push the file to the next number.
        Assert.Equal(1, result!.Unchanged);
        Assert.Equal(0, result.Renamed);
        Assert.True(File.Exists(stored.DestinationPath!));
    }

    [Fact]
    public async Task Rename_ReturnsNullForAnUnknownEvent()
        => Assert.Null(await (await CreateAsync()).Service.RenameAsync(Guid.NewGuid(), dryRun: false));

    private async Task<MediaFile> AddMediaAsync(Fixture fixture, int seconds)
    {
        var media = new MediaFile
        {
            SourceShareId = fixture.Media.SourceShareId,
            SourcePath = Path.Combine(
                Path.GetDirectoryName(fixture.Media.SourcePath)!,
                $"IMG2026081417{seconds:00}.jpg"),
            OriginalName = $"IMG2026081417{seconds:00}.jpg",
            Size = StoredContent.Length,
            Extension = ".jpg",
            MediaType = MediaType.Image,
            CapturedAt = fixture.Media.CapturedAt!.Value.AddSeconds(1),
            CapturedAtOffsetMinutes = 0,
            TimestampSource = fixture.Media.TimestampSource,
            CameraMake = fixture.Media.CameraMake,
            CameraModel = fixture.Media.CameraModel,
            FirstSeenAt = fixture.Media.FirstSeenAt,
            LastSeenAt = fixture.Media.LastSeenAt
        };
        await fixture.MediaFiles.UpsertAsync(media);
        return media;
    }

    private async Task<MediaOperation> StoreAsync(
        Fixture fixture,
        string storedName,
        bool withHash = true,
        MediaFile? media = null,
        int? offsetMinutes = 0)
    {
        var destinationFolder = Path.Combine(fixture.DestinationRoot, "Kroatien 2026");
        Directory.CreateDirectory(destinationFolder);
        var destinationPath = Path.Combine(destinationFolder, storedName);
        await File.WriteAllTextAsync(destinationPath, StoredContent);
        var hash = withHash ? await HashOfAsync(destinationPath) : null;
        var subject = media ?? fixture.Media;
        if (offsetMinutes != subject.CapturedAtOffsetMinutes)
        {
            await fixture.MediaFiles.UpsertAsync(CopyWithOffset(subject, offsetMinutes));
        }

        var operation = new MediaOperation
        {
            MediaFileId = subject.Id,
            EventId = fixture.Event.Id,
            State = MediaOperationState.Completed,
            SourcePath = subject.SourcePath,
            DestinationPath = destinationPath,
            SourceHash = hash,
            DestinationHash = hash,
            StartedAt = fixture.Event.StartAt,
            CompletedAt = fixture.Event.StartAt.AddMinutes(1)
        };
        await fixture.Operations.UpsertAsync(operation);
        return operation;
    }

    private static MediaFile CopyWithOffset(MediaFile source, int? offsetMinutes) => new()
    {
        Id = source.Id,
        SourceShareId = source.SourceShareId,
        SourcePath = source.SourcePath,
        OriginalName = source.OriginalName,
        Size = source.Size,
        Extension = source.Extension,
        MediaType = source.MediaType,
        CapturedAt = source.CapturedAt,
        TimestampSource = source.TimestampSource,
        CapturedAtOffsetMinutes = offsetMinutes,
        CameraMake = source.CameraMake,
        CameraModel = source.CameraModel,
        FirstSeenAt = source.FirstSeenAt,
        LastSeenAt = source.LastSeenAt
    };

    private static async Task<string> HashOfAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return await new Sha256HashService().ComputeSha256Async(stream);
    }

    private async Task<Fixture> CreateAsync(int? storedFileOffsetMinutes = null)
    {
        await new SqliteDatabaseInitializer(_factory).InitializeAsync();
        var shares = new SqliteShareRepository(_factory);
        var events = new SqliteMediaEventRepository(_factory);
        var groups = new SqliteSourceGroupRepository(_factory);
        var mediaFiles = new SqliteMediaFileRepository(_factory);
        var operations = new SqliteMediaOperationRepository(_factory);
        var presets = new SqliteRenamePresetRepository(_factory);
        var cameraMappings = new SqliteCameraMappingRepository(_factory);

        var sourceRoot = Path.Combine(_directory, "sources", "pavel");
        var destinationRoot = Path.Combine(_directory, "destinations", "family");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(destinationRoot);

        var preset = new RenamePreset { Name = "Phone Photos", Template = "{captured:yyyyMMdd_HHmmss}_{camera}" };
        await presets.UpsertAsync(preset);

        var sourceShare = new Share
        {
            Name = "Pavel",
            Path = sourceRoot,
            Role = ShareRole.Source,
            RenamePresetId = preset.Id,
            // UTC, so a name that comes out in +02:00 can only have come from the stored file's own
            // offset and not from the share's zone standing in for it.
            DefaultTimeZone = "UTC"
        };
        var destinationShare = new Share
        {
            Name = "Family",
            Path = destinationRoot,
            Role = ShareRole.Destination
        };
        await shares.UpsertAsync(sourceShare);
        await shares.UpsertAsync(destinationShare);

        var group = new SourceGroup { Name = "Phones", ShareIds = [sourceShare.Id] };
        await groups.UpsertAsync(group);

        var capturedAt = new DateTimeOffset(2026, 8, 14, 17, 47, 44, TimeSpan.Zero);
        var mediaEvent = new MediaEvent
        {
            Name = "Kroatien 2026",
            Type = "Vacation",
            StartAt = capturedAt.AddDays(-1),
            EndAt = capturedAt.AddDays(1),
            SourceGroupId = group.Id,
            DestinationShareId = destinationShare.Id,
            DestinationFolderTemplate = "{event.name}",
            OperationMode = OperationMode.SafeMove,
            Status = MediaEventStatus.Active
        };
        await events.UpsertAsync(mediaEvent);

        var media = new MediaFile
        {
            SourceShareId = sourceShare.Id,
            SourcePath = Path.Combine(sourceRoot, "IMG20260814174744.jpg"),
            OriginalName = "IMG20260814174744.jpg",
            Size = StoredContent.Length,
            Extension = ".jpg",
            MediaType = MediaType.Image,
            CapturedAt = capturedAt,
            CapturedAtOffsetMinutes = 0,
            TimestampSource = "DateTimeOriginal",
            CameraMake = "OnePlus",
            CameraModel = "OnePlus 12",
            FirstSeenAt = capturedAt,
            LastSeenAt = capturedAt
        };
        await mediaFiles.UpsertAsync(media);

        var service = new RoutedFileRenameService(
            operations,
            mediaFiles,
            events,
            shares,
            new RenameContextFactory(presets, cameraMappings),
            new LocalFileSystemGateway(),
            new Sha256HashService(),
            new StubExtractor(storedFileOffsetMinutes));

        return new Fixture(
            service,
            operations,
            presets,
            cameraMappings,
            mediaFiles,
            preset.Id,
            mediaEvent,
            media,
            destinationRoot);
    }

    private sealed record Fixture(
        RoutedFileRenameService Service,
        SqliteMediaOperationRepository Operations,
        SqliteRenamePresetRepository Presets,
        SqliteCameraMappingRepository CameraMappings,
        SqliteMediaFileRepository MediaFiles,
        Guid PresetId,
        MediaEvent Event,
        MediaFile Media,
        string DestinationRoot);

    /// <summary>
    /// Stands in for ExifTool reading the stored copy. Null means the file names no offset of its own,
    /// which is what an older camera or a stripped file looks like.
    /// </summary>
    private sealed class StubExtractor(int? offsetMinutes) : IMediaMetadataExtractor
    {
        public int Calls { get; private set; }

        public Task<MediaMetadata> ExtractAsync(
            Share share,
            string path,
            MediaType mediaType,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            var captured = offsetMinutes is { } minutes
                ? new DateTimeOffset(2026, 8, 14, 19, 47, 44, TimeSpan.FromMinutes(minutes))
                : (DateTimeOffset?)null;
            return Task.FromResult(new MediaMetadata(
                captured,
                captured is null ? null : "DateTimeOriginal",
                captured is null,
                // The stub stands for a file that states its own offset, or states none at all.
                captured?.Offset,
                "OnePlus",
                "OnePlus 12",
                null,
                null,
                null,
                "image/jpeg"));
        }
    }
}
