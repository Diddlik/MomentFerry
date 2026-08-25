using MomentFerry.Application.Abstractions;
using MomentFerry.Application.Services;
using MomentFerry.Core.Domain;
using MomentFerry.Infrastructure;

namespace MomentFerry.Tests;

public sealed class ShareDiscoveryServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "momentferry-discovery", Guid.NewGuid().ToString("N"));

    [Fact]
    public void EnumerateSeesEveryFileWhileScanStopsAtTheLimit()
    {
        Directory.CreateDirectory(root);
        for (var index = 0; index < 12; index++)
        {
            File.WriteAllText(Path.Combine(root, $"shot-{index:00}.jpg"), "x");
        }

        var service = new ShareDiscoveryService(
            new LocalFileSystemGateway(),
            new FixedClock(DateTimeOffset.UnixEpoch));
        var share = new Share { Name = "pavel", Path = root, Role = ShareRole.Source };

        Assert.Equal(12, service.Enumerate(share).Count());
        Assert.Equal(5, service.Scan(share, 5).Count);
    }

    [Fact]
    public void Enumerate_IgnoresSynologyMetadataDirectories()
    {
        var album = Directory.CreateDirectory(Path.Combine(root, "album"));
        var metadata = Directory.CreateDirectory(Path.Combine(album.FullName, "@eaDir", "photo.jpg"));
        File.WriteAllText(Path.Combine(album.FullName, "photo.jpg"), "photo");
        File.WriteAllText(Path.Combine(metadata.FullName, "SYNOFILE_THUMB_M.jpg"), "thumbnail");

        var service = new ShareDiscoveryService(
            new LocalFileSystemGateway(),
            new FixedClock(DateTimeOffset.UnixEpoch));
        var share = new Share { Name = "pavel", Path = root, Role = ShareRole.Source };

        var file = Assert.Single(service.Enumerate(share));
        Assert.Equal("album/photo.jpg", file.RelativePath);
    }

    [Fact]
    public void Observe_AppliesTheSameRulesAsEnumerateForASinglePath()
    {
        var album = Directory.CreateDirectory(Path.Combine(root, "album"));
        var photoPath = Path.Combine(album.FullName, "photo.jpg");
        File.WriteAllText(photoPath, "photo");
        var thumbnailDirectory = Directory.CreateDirectory(Path.Combine(album.FullName, "@eaDir", "photo.jpg"));
        var thumbnailPath = Path.Combine(thumbnailDirectory.FullName, "SYNOFILE_THUMB_M.jpg");
        File.WriteAllText(thumbnailPath, "thumbnail");
        var documentPath = Path.Combine(album.FullName, "notes.txt");
        File.WriteAllText(documentPath, "notes");

        var service = new ShareDiscoveryService(
            new LocalFileSystemGateway(),
            new FixedClock(DateTimeOffset.UnixEpoch));
        var share = new Share { Name = "pavel", Path = root, Role = ShareRole.Source };

        Assert.Equal("album/photo.jpg", service.Observe(share, photoPath)!.RelativePath);
        Assert.Null(service.Observe(share, thumbnailPath));
        Assert.Null(service.Observe(share, documentPath));
        Assert.Null(service.Observe(share, Path.Combine(album.FullName, "missing.jpg")));
    }

    [Fact]
    public void Enumerate_SkipsResilioPartialDownloadsAtAnyDepth()
    {
        // A transfer in progress is called "_.pending-<id>-<name>", which "*.!sync" never covered. A
        // stalled one looks stable after the share's window, and routing it would verify a truncated
        // file against its own checksum and then let Safe Move remove the partial.
        var album = Directory.CreateDirectory(Path.Combine(root, "Italy"));
        var photoPath = Path.Combine(album.FullName, "photo.jpg");
        File.WriteAllText(photoPath, "photo");
        var nestedPartial = Path.Combine(album.FullName, "_.pending-1781630915-IMG20260609192835.heic");
        File.WriteAllText(nestedPartial, "half a photo");
        var rootPartial = Path.Combine(root, "_.pending-1781288456-IMG20260605202055.heic");
        File.WriteAllText(rootPartial, "half a photo");

        var service = new ShareDiscoveryService(
            new LocalFileSystemGateway(),
            new FixedClock(DateTimeOffset.UnixEpoch));
        var share = new Share
        {
            Name = "pavel",
            Path = root,
            Role = ShareRole.Source,
            IgnorePatterns = SharePresets.Resilio.IgnorePatterns
        };

        var file = Assert.Single(service.Enumerate(share));
        Assert.Equal("Italy/photo.jpg", file.RelativePath);
        Assert.Null(service.Observe(share, nestedPartial));
        Assert.Null(service.Observe(share, rootPartial));
    }

    [Fact]
    public void Observe_RejectsPathsOutsideTheShare()
    {
        Directory.CreateDirectory(root);
        var outsideDirectory = Directory.CreateDirectory(Path.Combine(root, "..", "momentferry-outside-" + Guid.NewGuid().ToString("N")));
        var outsidePath = Path.Combine(outsideDirectory.FullName, "photo.jpg");
        File.WriteAllText(outsidePath, "photo");

        try
        {
            var service = new ShareDiscoveryService(
                new LocalFileSystemGateway(),
                new FixedClock(DateTimeOffset.UnixEpoch));
            var share = new Share { Name = "pavel", Path = root, Role = ShareRole.Source };

            Assert.Null(service.Observe(share, outsidePath));
        }
        finally
        {
            try { Directory.Delete(outsideDirectory.FullName, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Observe_ReportsWaitingStableUntilTheStabilityWindowElapses()
    {
        Directory.CreateDirectory(root);
        var photoPath = Path.Combine(root, "photo.jpg");
        File.WriteAllText(photoPath, "photo");

        var clock = new MutableClock(DateTimeOffset.UnixEpoch);
        var service = new ShareDiscoveryService(new LocalFileSystemGateway(), clock);
        var share = new Share { Name = "pavel", Path = root, Role = ShareRole.Source, StabilitySeconds = 30 };

        Assert.Equal(DiscoveryState.WaitingStable, service.Observe(share, photoPath)!.State);

        clock.UtcNow = DateTimeOffset.UnixEpoch.AddSeconds(31);
        Assert.Equal(DiscoveryState.Stable, service.Observe(share, photoPath)!.State);
    }

    private sealed class MutableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    [Fact]
    public void Enumerate_UsesTheShareOwnExtensionListWhenSet()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "keep.jpg"), "x");
        File.WriteAllText(Path.Combine(root, "skip.png"), "x");
        File.WriteAllText(Path.Combine(root, "clip.mkv"), "x");

        var service = new ShareDiscoveryService(
            new LocalFileSystemGateway(),
            new FixedClock(DateTimeOffset.UnixEpoch));
        var share = new Share
        {
            Name = "narrow",
            Path = root,
            Role = ShareRole.Source,
            ImageExtensions = [".jpg"],
            VideoExtensions = [".mkv"]
        };

        var names = service.Enumerate(share).Select(x => x.RelativePath).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "clip.mkv", "keep.jpg" }, names);
    }

    [Fact]
    public void Enumerate_FallsBackToBuiltInExtensionsWhenShareListIsEmpty()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "photo.png"), "x");
        File.WriteAllText(Path.Combine(root, "notes.txt"), "x");

        var service = new ShareDiscoveryService(
            new LocalFileSystemGateway(),
            new FixedClock(DateTimeOffset.UnixEpoch));
        var share = new Share { Name = "default", Path = root, Role = ShareRole.Source };

        var file = Assert.Single(service.Enumerate(share));
        Assert.Equal("photo.png", file.RelativePath);
    }

    [Theory]
    [InlineData("jpg", ".jpg")]
    [InlineData(".JPG", ".jpg")]
    [InlineData("*.Mp4", ".mp4")]
    [InlineData("  .heic  ", ".heic")]
    public void NormalizeExtensions_AcceptsCommonUserInput(string input, string expected)
    {
        Assert.Equal([expected], MediaExtensionDefaults.Normalize([input]));
    }

    [Fact]
    public void NormalizeExtensions_DropsBlanksAndDuplicates()
    {
        Assert.Equal(
            [".jpg", ".png"],
            MediaExtensionDefaults.Normalize([".jpg", "", "  ", "JPG", ".png", "."]));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
