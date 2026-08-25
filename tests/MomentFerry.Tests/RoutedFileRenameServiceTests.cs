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

    private async Task<MediaOperation> StoreAsync(Fixture fixture, string storedName, bool withHash = true)
    {
        var destinationFolder = Path.Combine(fixture.DestinationRoot, "Kroatien 2026");
        Directory.CreateDirectory(destinationFolder);
        var destinationPath = Path.Combine(destinationFolder, storedName);
        await File.WriteAllTextAsync(destinationPath, StoredContent);
        var hash = withHash ? await HashOfAsync(destinationPath) : null;

        var operation = new MediaOperation
        {
            MediaFileId = fixture.Media.Id,
            EventId = fixture.Event.Id,
            State = MediaOperationState.Completed,
            SourcePath = fixture.Media.SourcePath,
            DestinationPath = destinationPath,
            SourceHash = hash,
            DestinationHash = hash,
            StartedAt = fixture.Event.StartAt,
            CompletedAt = fixture.Event.StartAt.AddMinutes(1)
        };
        await fixture.Operations.UpsertAsync(operation);
        return operation;
    }

    private static async Task<string> HashOfAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return await new Sha256HashService().ComputeSha256Async(stream);
    }

    private async Task<Fixture> CreateAsync()
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
            RenamePresetId = preset.Id
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
            new Sha256HashService());

        return new Fixture(
            service,
            operations,
            presets,
            cameraMappings,
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
        Guid PresetId,
        MediaEvent Event,
        MediaFile Media,
        string DestinationRoot);
}
