using System.Diagnostics;
using MomentFerry.Application.Abstractions;
using MomentFerry.Application.Services;
using MomentFerry.Core.Domain;

namespace MomentFerry.Web.Background;

/// <summary>
/// Runs routing previews outside the HTTP request that asked for one. A preview reads metadata for
/// every unindexed file in a share, which on a NAS takes minutes, and a reverse proxy in front of the
/// container gives up long before that: it answered the browser with 504 and the cancelled request
/// threw away every metadata read it had done, so a retry started from nothing again. As a job the
/// work outlives the connection, the browser polls its progress, and only the application stopping
/// cancels it.
///
/// One job per share: starting a preview while one is running for that share returns the running job
/// rather than reading the same files twice. The latest result per share is kept until the next start.
/// </summary>
public sealed class RoutingPreviewJobs(
    RoutingPreviewService routing,
    IRuntimeSettingsStore runtimeSettings,
    IHostApplicationLifetime lifetime,
    ILogger<RoutingPreviewJobs> logger)
{
    private const int FileLimit = 2000;
    private readonly Dictionary<Guid, RoutingPreviewJob> _byShare = [];
    private readonly Lock _gate = new();

    public RoutingPreviewJob Start(Share share)
    {
        lock (_gate)
        {
            if (_byShare.TryGetValue(share.Id, out var existing) && existing.CompletedAt is null) return existing;

            var job = new RoutingPreviewJob(share.Id, share.Name);
            _byShare[share.Id] = job;
            _ = Task.Run(() => RunAsync(job, share));
            return job;
        }
    }

    public RoutingPreviewJob? Find(Guid jobId)
    {
        lock (_gate)
        {
            return _byShare.Values.FirstOrDefault(x => x.Id == jobId);
        }
    }

    private async Task RunAsync(RoutingPreviewJob job, Share share)
    {
        var stopwatch = Stopwatch.StartNew();
        logger.LogInformation("Routing preview started for {Share}", share.Name);
        try
        {
            var settings = await runtimeSettings.GetAsync(lifetime.ApplicationStopping);
            var items = await routing.PreviewAsync(
                share,
                FileLimit,
                lifetime.ApplicationStopping,
                settings.MaxParallelMetadataReads,
                progress => job.Progress = progress);

            var result = new RoutingPreviewResult(
                share.Id,
                share.Name,
                items.Count,
                items.Count(x => x.State == RoutingPreviewState.Matched),
                items.Count(x => x.State == RoutingPreviewState.Unmatched),
                items.Count(x => x.State == RoutingPreviewState.Ambiguous),
                items);
            job.Complete(result, null);
            logger.LogInformation(
                "Routing preview for {Share} finished in {Seconds:F1}s: {Total} files, {Matched} matched, {Unmatched} outside any event, {Ambiguous} matching several events",
                share.Name,
                stopwatch.Elapsed.TotalSeconds,
                result.Total,
                result.Matched,
                result.Unmatched,
                result.Ambiguous);
        }
        catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested)
        {
            job.Complete(null, "MomentFerry stopped before the preview finished.");
            logger.LogInformation("Routing preview for {Share} cancelled by shutdown", share.Name);
        }
        catch (Exception ex)
        {
            job.Complete(null, ex.Message);
            logger.LogError(ex, "Routing preview for {Share} failed after {Seconds:F1}s", share.Name, stopwatch.Elapsed.TotalSeconds);
        }
    }
}

public sealed class RoutingPreviewJob(Guid shareId, string shareName)
{
    // Written from the metadata reads in parallel and read by polling requests; each is replaced as a
    // whole, so a reader never sees a completion time without the result or error that goes with it.
    private volatile RoutingPreviewProgress? _progress;
    private volatile Outcome? _outcome;

    public Guid Id { get; } = Guid.NewGuid();
    public Guid ShareId { get; } = shareId;
    public string ShareName { get; } = shareName;
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    public RoutingPreviewProgress? Progress
    {
        get => _progress;
        set => _progress = value;
    }

    public DateTimeOffset? CompletedAt => _outcome?.CompletedAt;
    public string? Error => _outcome?.Error;
    public RoutingPreviewResult? Result => _outcome?.Result;

    internal void Complete(RoutingPreviewResult? result, string? error) =>
        _outcome = new Outcome(DateTimeOffset.UtcNow, result, error);

    private sealed record Outcome(DateTimeOffset CompletedAt, RoutingPreviewResult? Result, string? Error);
}

public sealed record RoutingPreviewResult(
    Guid Id,
    string Name,
    int Total,
    int Matched,
    int Unmatched,
    int Ambiguous,
    IReadOnlyList<RoutingPreviewItem> Items);
