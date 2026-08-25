using MomentFerry.Application.Abstractions;
using MomentFerry.Application.Services;
using MomentFerry.Core.Domain;
using MomentFerry.Infrastructure;
using MomentFerry.Infrastructure.Persistence;
using System.Collections.Concurrent;

namespace MomentFerry.Tests;

public sealed class RoutingPreviewServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "momentferry-routing", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PreviewAsync_RotatesAcrossLargeSourceAndPersistsProgress()
    {
        var sourcePath = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        var destinationPath = Directory.CreateDirectory(Path.Combine(root, "destination")).FullName;
        foreach (var name in new[] { "001.jpg", "002.jpg", "003.jpg" })
        {
            File.WriteAllText(Path.Combine(sourcePath, name), name);
        }

        var factory = new SqliteConnectionFactory(Path.Combine(root, "momentferry.db"));
        await new SqliteDatabaseInitializer(factory).InitializeAsync();
        var mediaFiles = new SqliteMediaFileRepository(factory);
        var events = new SqliteMediaEventRepository(factory);
        var groups = new SqliteSourceGroupRepository(factory);
        var shares = new SqliteShareRepository(factory);
        var source = new Share
        {
            Name = "Phone",
            Path = sourcePath,
            Role = ShareRole.Source,
            StabilitySeconds = 0
        };
        var destination = new Share
        {
            Name = "Family",
            Path = destinationPath,
            Role = ShareRole.Destination
        };
        await shares.UpsertAsync(source);
        await shares.UpsertAsync(destination);
        var group = new SourceGroup { Name = "Parents", ShareIds = [source.Id] };
        await groups.UpsertAsync(group);
        await events.UpsertAsync(new MediaEvent
        {
            Name = "Vacation",
            StartAt = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
            Status = MediaEventStatus.Active,
            SourceGroupId = group.Id,
            DestinationShareId = destination.Id
        });

        var clock = new MutableClock(new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero));
        var selected = new List<string>();
        for (var cycle = 0; cycle < 3; cycle++)
        {
            var service = CreateService(factory, mediaFiles, events, groups, shares, clock);
            var item = Assert.Single(await service.PreviewAsync(source, 1));
            Assert.Equal(RoutingPreviewState.Matched, item.State);
            selected.Add(item.MediaFile.OriginalName);
            clock.UtcNow = clock.UtcNow.AddMinutes(1);
        }

        Assert.Equal(new[] { "001.jpg", "002.jpg", "003.jpg" }, selected);

        var restarted = CreateService(factory, mediaFiles, events, groups, shares, clock);
        var next = Assert.Single(await restarted.PreviewAsync(source, 1));
        Assert.Equal("001.jpg", next.MediaFile.OriginalName);
    }

    [Fact]
    public async Task PreviewAsync_ReusesMetadataUntilSourceChanges()
    {
        var sourcePath = Directory.CreateDirectory(Path.Combine(root, "cache-source")).FullName;
        var filePath = Path.Combine(sourcePath, "photo.jpg");
        File.WriteAllText(filePath, "photo");
        var factory = new SqliteConnectionFactory(Path.Combine(root, "cache.db"));
        await new SqliteDatabaseInitializer(factory).InitializeAsync();
        var mediaFiles = new SqliteMediaFileRepository(factory);
        var shares = new SqliteShareRepository(factory);
        var source = new Share { Name = "Phone", Path = sourcePath, Role = ShareRole.Source, StabilitySeconds = 0 };
        await shares.UpsertAsync(source);
        var clock = new MutableClock(new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero));
        var extractor = new CountingMetadataExtractor(clock);
        var service = CreateService(factory, mediaFiles, new SqliteMediaEventRepository(factory), new SqliteSourceGroupRepository(factory), shares, clock, extractor);

        await service.PreviewAsync(source, 1);
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        await service.PreviewAsync(source, 1);

        Assert.Equal(1, extractor.Calls);

        File.SetLastWriteTimeUtc(filePath, clock.UtcNow.AddMinutes(1).UtcDateTime);
        await service.PreviewAsync(source, 1);
        Assert.Equal(2, extractor.Calls);
    }

    [Fact]
    public async Task PreviewAsync_BoundsParallelMetadataReadsAndReportsProgress()
    {
        var sourcePath = Directory.CreateDirectory(Path.Combine(root, "parallel-source")).FullName;
        foreach (var name in new[] { "1.jpg", "2.jpg", "3.jpg", "4.jpg" })
            File.WriteAllText(Path.Combine(sourcePath, name), name);
        var factory = new SqliteConnectionFactory(Path.Combine(root, "parallel.db"));
        await new SqliteDatabaseInitializer(factory).InitializeAsync();
        var mediaFiles = new SqliteMediaFileRepository(factory);
        var shares = new SqliteShareRepository(factory);
        var source = new Share { Name = "Phone", Path = sourcePath, Role = ShareRole.Source, StabilitySeconds = 0 };
        await shares.UpsertAsync(source);
        var clock = new MutableClock(new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero));
        var extractor = new CountingMetadataExtractor(clock, delayMilliseconds: 30);
        var progress = new ConcurrentQueue<RoutingPreviewProgress>();
        var service = CreateService(factory, mediaFiles, new SqliteMediaEventRepository(factory), new SqliteSourceGroupRepository(factory), shares, clock, extractor);

        await service.PreviewAsync(source, 4, maxParallelMetadataReads: 2, progress: progress.Enqueue);

        Assert.Equal(2, extractor.MaxConcurrent);
        Assert.Contains(progress, item => item.Phase == "Matching events" && item.Processed == 4 && item.Total == 4);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    private static RoutingPreviewService CreateService(
        SqliteConnectionFactory factory,
        IMediaFileRepository mediaFiles,
        IMediaEventRepository events,
        ISourceGroupRepository groups,
        IShareRepository shares,
        IClock clock,
        IMediaMetadataExtractor? extractor = null) => new(
            new ShareDiscoveryService(new LocalFileSystemGateway(), clock),
            extractor ?? new FixedMetadataExtractor(clock),
            mediaFiles,
            events,
            groups,
            shares,
            new DestinationPathResolver(new LocalFileSystemGateway()),
            new RenameContextFactory(
                new SqliteRenamePresetRepository(factory),
                new SqliteCameraMappingRepository(factory)),
            clock);

    private sealed class FixedMetadataExtractor(IClock clock) : IMediaMetadataExtractor
    {
        public Task<MediaMetadata> ExtractAsync(
            Share share,
            string path,
            MediaType mediaType,
            CancellationToken cancellationToken = default) => Task.FromResult(new MediaMetadata(
                clock.UtcNow,
                "DateTimeOriginal",
                false,
                TimeSpan.Zero,
                null,
                null,
                null,
                null,
                null,
                "image/jpeg"));
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    private sealed class CountingMetadataExtractor(IClock clock, int delayMilliseconds = 0) : IMediaMetadataExtractor
    {
        private int _active;
        private int _calls;
        private int _maxConcurrent;

        public int Calls => _calls;
        public int MaxConcurrent => _maxConcurrent;

        public async Task<MediaMetadata> ExtractAsync(
            Share share,
            string path,
            MediaType mediaType,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            var active = Interlocked.Increment(ref _active);
            int observed;
            do
            {
                observed = _maxConcurrent;
            } while (active > observed && Interlocked.CompareExchange(ref _maxConcurrent, active, observed) != observed);
            try
            {
                if (delayMilliseconds > 0) await Task.Delay(delayMilliseconds, cancellationToken);
                return new MediaMetadata(clock.UtcNow, "DateTimeOriginal", false, TimeSpan.Zero, null, null, null, null, null, "image/jpeg");
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }
}
