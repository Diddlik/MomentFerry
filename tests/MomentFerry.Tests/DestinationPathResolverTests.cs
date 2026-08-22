using MomentFerry.Application.Services;
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

    private static MediaFile MediaFileOf(Share source, MediaType mediaType, string name) => new()
    {
        SourceShareId = source.Id,
        SourcePath = Path.Combine(source.Path, name),
        OriginalName = name,
        Size = 1,
        Extension = Path.GetExtension(name),
        MediaType = mediaType,
        CapturedAt = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero),
        FirstSeenAt = DateTimeOffset.UnixEpoch,
        LastSeenAt = DateTimeOffset.UnixEpoch
    };
}
