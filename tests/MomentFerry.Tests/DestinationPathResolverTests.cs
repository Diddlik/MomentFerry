using MomentFerry.Application.Services;
using MomentFerry.Infrastructure;
using MomentFerry.Core.Domain;

namespace MomentFerry.Tests;

public sealed class DestinationPathResolverTests
{
    [Fact]
    public void Resolve_ExpandsTemplateInsideDestinationRoot()
    {
        var resolver = new DestinationPathResolver();
        var root = Path.Combine(Path.GetTempPath(), "momentferry-tests", Guid.NewGuid().ToString("N"));
        var source = new Share
        {
            Name = "Phone A",
            Path = Path.Combine(root, "source"),
            Role = ShareRole.Source,
            Owner = "Pavel"
        };
        var destination = new Share
        {
            Name = "Family",
            Path = Path.Combine(root, "destination"),
            Role = ShareRole.Destination
        };
        var mediaEvent = new MediaEvent
        {
            Name = "Italy 2026",
            Type = "Vacation",
            StartAt = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
            SourceGroupId = Guid.NewGuid(),
            DestinationShareId = destination.Id,
            DestinationFolderTemplate = "{year}/{event.name}/{owner}"
        };
        var media = new MediaFile
        {
            SourceShareId = source.Id,
            SourcePath = Path.Combine(source.Path, "IMG_0001.jpg"),
            OriginalName = "IMG_0001.jpg",
            Size = 123,
            Extension = ".jpg",
            MediaType = MediaType.Image,
            CapturedAt = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero),
            FirstSeenAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow
        };

        var result = resolver.Resolve(mediaEvent, source, destination, media);

        Assert.Equal(
            Path.GetFullPath(Path.Combine(destination.Path, "2026", "Italy 2026", "Pavel", "IMG_0001.jpg")),
            result);
    }

    [Fact]
    public void EnsureInsideRoot_RejectsEscapingPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "momentferry-tests", Guid.NewGuid().ToString("N"), "destination");
        var escaped = Path.Combine(root, "..", "outside", "photo.jpg");

        Assert.Throws<InvalidOperationException>(() => DestinationPathResolver.EnsureInsideRoot(root, escaped));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData("   ")]
    public void SafeSegment_DoesNotReturnUnsafeSpecialSegments(string value)
    {
        var result = DestinationPathResolver.SafeSegment(value);

        Assert.False(string.IsNullOrWhiteSpace(result));
        Assert.NotEqual(".", result);
        Assert.NotEqual("..", result);
    }
    [Theory]
    [InlineData(MediaType.Image, "Photos")]
    [InlineData(MediaType.Video, "Clips")]
    public void Resolve_PlacesMediaInItsConfiguredSubfolder(MediaType mediaType, string expectedFolder)
    {
        var (resolver, source, destination, mediaEvent) = Scenario(imageSubfolder: "Photos", videoSubfolder: "Clips");
        var mediaFile = MediaFileOf(source, mediaType, "shot.bin");

        var resolved = resolver.Resolve(mediaEvent, source, destination, mediaFile);

        Assert.Equal(
            Path.GetFullPath(Path.Combine(destination.Path, "Italy 2026", expectedFolder, "shot.bin")),
            resolved);
    }

    [Fact]
    public void Resolve_KeepsMediaTogetherWhenNoSubfolderIsConfigured()
    {
        var (resolver, source, destination, mediaEvent) = Scenario(imageSubfolder: null, videoSubfolder: null);

        var image = resolver.Resolve(mediaEvent, source, destination, MediaFileOf(source, MediaType.Image, "a.jpg"));
        var video = resolver.Resolve(mediaEvent, source, destination, MediaFileOf(source, MediaType.Video, "b.mp4"));

        Assert.Equal(Path.GetFullPath(Path.Combine(destination.Path, "Italy 2026", "a.jpg")), image);
        Assert.Equal(Path.GetFullPath(Path.Combine(destination.Path, "Italy 2026", "b.mp4")), video);
    }

    [Fact]
    public void Resolve_SupportsNestedSubfoldersAndRejectsTraversal()
    {
        var (resolver, source, destination, mediaEvent) = Scenario(imageSubfolder: "Media/Photos", videoSubfolder: "../escape");

        var image = resolver.Resolve(mediaEvent, source, destination, MediaFileOf(source, MediaType.Image, "a.jpg"));
        Assert.Equal(
            Path.GetFullPath(Path.Combine(destination.Path, "Italy 2026", "Media", "Photos", "a.jpg")),
            image);

        // '..' is sanitized into a literal segment rather than escaping the destination root.
        var video = resolver.Resolve(mediaEvent, source, destination, MediaFileOf(source, MediaType.Video, "b.mp4"));
        DestinationPathResolver.EnsureInsideRoot(destination.Path, video);
        Assert.DoesNotContain("..", Path.GetRelativePath(destination.Path, video));
    }

    [Fact]
    public void Resolve_AppliesSourcePresetThenDestinationPreset()
    {
        var (resolver, source, destination, mediaEvent) = Scenario(null, null);
        var sourcePreset = new RenamePreset { Name = "normalize", Template = "{captured:yyyyMMdd_HHmmss}" };
        var destinationPreset = new RenamePreset { Name = "decorate", Template = "{name}_{camera}" };

        source = CopyWithPreset(source, sourcePreset.Id);
        destination = CopyWithPreset(destination, destinationPreset.Id);

        var rename = new RenameContext(
            new Dictionary<Guid, RenamePreset> { [sourcePreset.Id] = sourcePreset, [destinationPreset.Id] = destinationPreset },
            FileNameTemplate.BuildCameraNames([new CameraMapping { From = "CPH2581", To = "OnePlus12" }]));

        var media = MediaFileOf(source, MediaType.Image, "img20260216_123056.jpg", "OnePlus", "CPH2581");

        var resolved = resolver.Resolve(mediaEvent, source, destination, media, rename);

        Assert.Equal("20260810_120000_OnePlus12.jpg", Path.GetFileName(resolved));
    }

    [Fact]
    public void Resolve_NamesAFileWithTheWallClockTimeTheCameraRecorded()
    {
        // The reported case: a Samsung photo whose EXIF says 13:52:53 with OffsetTimeOriginal +02:00.
        // CapturedAt is normalised to UTC for event matching, so naming straight from it produced
        // 20260821_115253 - two hours before the time on the photo.
        var (resolver, source, destination, mediaEvent) = Scenario(null, null);
        var preset = new RenamePreset { Name = "phone", Template = "{captured:yyyyMMdd_HHmmss}_{camera}" };
        source = CopyWithPreset(source, preset.Id);
        var rename = new RenameContext(
            new Dictionary<Guid, RenamePreset> { [preset.Id] = preset },
            FileNameTemplate.BuildCameraNames([new CameraMapping { From = "Galaxy S25", To = "GalaxyS25" }]));

        var media = MediaFileOf(
            source,
            MediaType.Image,
            "20260821_135253.jpg",
            "samsung",
            "Galaxy S25",
            capturedAt: new DateTimeOffset(2026, 8, 21, 11, 52, 53, TimeSpan.Zero),
            capturedAtOffsetMinutes: 120);

        var resolved = resolver.Resolve(mediaEvent, source, destination, media, rename);

        Assert.Equal("20260821_135253_GalaxyS25.jpg", Path.GetFileName(resolved));
    }

    [Fact]
    public void Resolve_FallsBackToTheShareZoneWhenTheFileNamedNoOffset()
    {
        // Nothing recorded an offset, so the share's zone stands in - the same assumption the
        // extractor makes on the way in, which makes this the inverse of it rather than a new guess.
        var (resolver, source, destination, mediaEvent) = Scenario(null, null);
        source = CopyWithZone(source, "Europe/Berlin");
        var preset = new RenamePreset { Name = "phone", Template = "{captured:yyyyMMdd_HHmmss}" };
        source = CopyWithPreset(source, preset.Id);
        var rename = new RenameContext(
            new Dictionary<Guid, RenamePreset> { [preset.Id] = preset },
            FileNameTemplate.BuildCameraNames([]));

        var media = MediaFileOf(
            source,
            MediaType.Image,
            "a.jpg",
            capturedAt: new DateTimeOffset(2026, 8, 21, 11, 52, 53, TimeSpan.Zero),
            capturedAtOffsetMinutes: null);

        var resolved = resolver.Resolve(mediaEvent, source, destination, media, rename);

        Assert.Equal("20260821_135253.jpg", Path.GetFileName(resolved));
    }

    [Fact]
    public void Resolve_KeepsTheOriginalNameWhenNeitherShareHasAPreset()
    {
        var (resolver, source, destination, mediaEvent) = Scenario(null, null);
        var media = MediaFileOf(source, MediaType.Image, "img20260216_123056.jpg");

        var resolved = resolver.Resolve(mediaEvent, source, destination, media, RenameContext.Empty);

        Assert.Equal("img20260216_123056.jpg", Path.GetFileName(resolved));
    }

    [Fact]
    public void Resolve_AdvancesTheSequenceUntilTheNameIsFree()
    {
        var (_, source, destination, mediaEvent) = Scenario(null, null);
        var preset = new RenamePreset { Name = "numbered", Template = "{captured:yyyyMMdd}_{seq:0000}" };
        destination = CopyWithPreset(destination, preset.Id);
        var rename = new RenameContext(
            new Dictionary<Guid, RenamePreset> { [preset.Id] = preset },
            FileNameTemplate.BuildCameraNames([]));

        var targetDirectory = Path.Combine(destination.Path, "Italy 2026");
        Directory.CreateDirectory(targetDirectory);
        try
        {
            File.WriteAllText(Path.Combine(targetDirectory, "20260810_0001.jpg"), "taken");
            var resolver = new DestinationPathResolver(new LocalFileSystemGateway());
            var media = MediaFileOf(source, MediaType.Image, "whatever.jpg");

            var resolved = resolver.Resolve(mediaEvent, source, destination, media, rename);

            Assert.Equal("20260810_0002.jpg", Path.GetFileName(resolved));
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(destination.Path)!, recursive: true); } catch { }
        }
    }

    private static Share CopyWithPreset(Share share, Guid presetId) => new()
    {
        Id = share.Id,
        Name = share.Name,
        Path = share.Path,
        Role = share.Role,
        Owner = share.Owner,
        ImageSubfolder = share.ImageSubfolder,
        VideoSubfolder = share.VideoSubfolder,
        RenamePresetId = presetId
    };

    private static Share CopyWithZone(Share share, string timeZone) => new()
    {
        Id = share.Id,
        Name = share.Name,
        Path = share.Path,
        Role = share.Role,
        Owner = share.Owner,
        ImageSubfolder = share.ImageSubfolder,
        VideoSubfolder = share.VideoSubfolder,
        RenamePresetId = share.RenamePresetId,
        DefaultTimeZone = timeZone
    };

    private static (DestinationPathResolver, Share, Share, MediaEvent) Scenario(
        string? imageSubfolder,
        string? videoSubfolder)
    {
        var root = Path.Combine(Path.GetTempPath(), "momentferry-tests", Guid.NewGuid().ToString("N"));
        var source = new Share { Name = "Phone A", Path = Path.Combine(root, "source"), Role = ShareRole.Source };
        var destination = new Share
        {
            Name = "Family",
            Path = Path.Combine(root, "destination"),
            Role = ShareRole.Destination,
            ImageSubfolder = imageSubfolder,
            VideoSubfolder = videoSubfolder
        };
        var mediaEvent = new MediaEvent
        {
            Name = "Italy 2026",
            StartAt = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
            SourceGroupId = Guid.NewGuid(),
            DestinationShareId = destination.Id,
            DestinationFolderTemplate = "{event.name}"
        };
        return (new DestinationPathResolver(), source, destination, mediaEvent);
    }

    private static MediaFile MediaFileOf(
        Share source,
        MediaType mediaType,
        string name,
        string? cameraMake = null,
        string? cameraModel = null,
        DateTimeOffset? capturedAt = null,
        int? capturedAtOffsetMinutes = 0) => new()
        {
            CameraMake = cameraMake,
            CameraModel = cameraModel,
            SourceShareId = source.Id,
            SourcePath = Path.Combine(source.Path, name),
            OriginalName = name,
            Size = 1,
            Extension = Path.GetExtension(name),
            MediaType = mediaType,
            CapturedAt = capturedAt ?? new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero),
            // Pinned by default, so a name never depends on the zone the test host happens to run in.
            CapturedAtOffsetMinutes = capturedAtOffsetMinutes,
            FirstSeenAt = DateTimeOffset.UnixEpoch,
            LastSeenAt = DateTimeOffset.UnixEpoch
        };
}
