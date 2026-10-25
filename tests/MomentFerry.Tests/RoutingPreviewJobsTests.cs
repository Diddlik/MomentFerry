using Microsoft.Extensions.Hosting;
using MomentFerry.Application.Abstractions;
using MomentFerry.Application.Services;
using MomentFerry.Core.Domain;
using MomentFerry.Infrastructure;
using MomentFerry.Infrastructure.Persistence;
using MomentFerry.Web.Background;

namespace MomentFerry.Tests;

public sealed class RoutingPreviewJobsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "momentferry-preview-jobs", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Start_RunsThePreviewOutsideTheCallerAndKeepsOneJobPerShare()
    {
        var extractor = new GatedExtractor();
        var (jobs, source) = await CreateAsync(extractor);

        var job = jobs.Start(source);
        var again = jobs.Start(source);

        Assert.Same(job, again);
        Assert.Null(job.CompletedAt);

        extractor.Release();
        await WaitForCompletionAsync(job);

        Assert.Null(job.Error);
        Assert.Equal(2, job.Result!.Total);
        Assert.Equal(2, job.Result.Matched);
        Assert.Same(job, jobs.Find(job.Id));
        Assert.NotSame(job, jobs.Start(source));
    }

    [Fact]
    public async Task Start_RecordsAFailureInsteadOfLosingIt()
    {
        var extractor = new GatedExtractor(new IOException("ExifTool is not installed."));
        var (jobs, source) = await CreateAsync(extractor);

        var job = jobs.Start(source);
        extractor.Release();
        await WaitForCompletionAsync(job);

        Assert.Null(job.Result);
        Assert.Equal("ExifTool is not installed.", job.Error);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    private static async Task WaitForCompletionAsync(RoutingPreviewJob job)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (job.CompletedAt is null && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.NotNull(job.CompletedAt);
    }

    private async Task<(RoutingPreviewJobs Jobs, Share Source)> CreateAsync(IMediaMetadataExtractor extractor)
    {
        var sourcePath = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        var destinationPath = Directory.CreateDirectory(Path.Combine(root, "destination")).FullName;
        File.WriteAllText(Path.Combine(sourcePath, "001.jpg"), "1");
        File.WriteAllText(Path.Combine(sourcePath, "002.jpg"), "2");

        var factory = new SqliteConnectionFactory(Path.Combine(root, "momentferry.db"));
        await new SqliteDatabaseInitializer(factory).InitializeAsync();
        var mediaFiles = new SqliteMediaFileRepository(factory);
        var events = new SqliteMediaEventRepository(factory);
        var groups = new SqliteSourceGroupRepository(factory);
        var shares = new SqliteShareRepository(factory);
        var source = new Share { Name = "Phone", Path = sourcePath, Role = ShareRole.Source, StabilitySeconds = 0 };
        var destination = new Share { Name = "Family", Path = destinationPath, Role = ShareRole.Destination };
        await shares.UpsertAsync(source);
        await shares.UpsertAsync(destination);
        var group = new SourceGroup { Name = "Parents", ShareIds = [source.Id] };
        await groups.UpsertAsync(group);
        await events.UpsertAsync(new MediaEvent
        {
            Name = "Vacation",
            StartAt = GatedExtractor.CapturedAt.AddDays(-1),
            Status = MediaEventStatus.Active,
            SourceGroupId = group.Id,
            DestinationShareId = destination.Id
        });

        var clock = new FixedClock(GatedExtractor.CapturedAt.AddDays(1));
        var routing = new RoutingPreviewService(
            new ShareDiscoveryService(new LocalFileSystemGateway(), clock),
            extractor,
            mediaFiles,
            events,
            groups,
            shares,
            new DestinationPathResolver(new LocalFileSystemGateway()),
            new RenameContextFactory(new SqliteRenamePresetRepository(factory), new SqliteCameraMappingRepository(factory)),
            clock,
            NullLogger<RoutingPreviewService>.Instance);

        var jobs = new RoutingPreviewJobs(
            routing,
            new StubSettingsStore(),
            new StubLifetime(),
            NullLogger<RoutingPreviewJobs>.Instance);
        return (jobs, source);
    }

    /// <summary>Holds every metadata read until released, so a test can observe the job while it runs.</summary>
    private sealed class GatedExtractor(Exception? failure = null) : IMediaMetadataExtractor
    {
        public static readonly DateTimeOffset CapturedAt = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate.TrySetResult();

        public async Task<MediaMetadata> ExtractAsync(
            Share share,
            string path,
            MediaType mediaType,
            CancellationToken cancellationToken = default)
        {
            await _gate.Task.WaitAsync(cancellationToken);
            if (failure is not null) throw failure;
            return new MediaMetadata(CapturedAt, "DateTimeOriginal", false, TimeSpan.Zero, null, null, null, null, null, "image/jpeg");
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class StubSettingsStore : IRuntimeSettingsStore
    {
        public Task<MomentFerryRuntimeSettings> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new MomentFerryRuntimeSettings());

        public Task<MomentFerryRuntimeSettings> UpdateAsync(MomentFerryRuntimeSettings settings, CancellationToken cancellationToken = default) =>
            Task.FromResult(settings);

        public Task<MomentFerryRuntimeSettings> ResetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new MomentFerryRuntimeSettings());
    }

    private sealed class StubLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
