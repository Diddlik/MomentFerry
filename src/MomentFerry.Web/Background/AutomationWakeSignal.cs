using System.Threading.Channels;

namespace MomentFerry.Web.Background;

/// <summary>
/// What a wake-up asks the routing worker to do. Targeted requests carry the paths the filesystem
/// watcher already identified, so the worker can evaluate them without re-walking the whole share.
/// </summary>
public sealed record AutomationWakeRequest(
    bool FullReconcile,
    IReadOnlyDictionary<Guid, IReadOnlyCollection<string>> TargetedPaths,
    IReadOnlyCollection<Guid> BackfillEventIds)
{
    public static readonly AutomationWakeRequest None = new(
        false,
        new Dictionary<Guid, IReadOnlyCollection<string>>(),
        []);

    public bool HasWork => FullReconcile || TargetedPaths.Count > 0 || BackfillEventIds.Count > 0;
}

public sealed class AutomationWakeSignal
{
    // Capacity 1 with DropWrite: the channel only signals that work is pending. The work itself
    // accumulates under _gate, so a coalesced wake can never discard a changed path.
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false
    });

    // Past this many pending paths for one share, a bounded full walk is cheaper than tracking them,
    // and it restores the MaxFilesPerSharePerCycle limit that targeted evaluation does not apply.
    private const int MaxPendingPathsPerShare = 1000;

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, HashSet<string>> _pendingPaths = [];
    private readonly HashSet<Guid> _pendingBackfills = [];
    private bool _fullReconcileRequested;

    /// <summary>Requests a full share walk, used by the periodic sweep and by watcher failures.</summary>
    public void Wake()
    {
        lock (_gate)
        {
            _fullReconcileRequested = true;
        }

        _channel.Writer.TryWrite(true);
    }

    /// <summary>
    /// Requests a full pass over one event's source shares, routing everything that falls inside its
    /// capture window. Used to apply an event defined after the media already arrived.
    /// </summary>
    public void WakeForBackfill(Guid eventId)
    {
        lock (_gate)
        {
            _pendingBackfills.Add(eventId);
        }

        _channel.Writer.TryWrite(true);
    }

    /// <summary>Requests evaluation of a single changed path, avoiding a full share walk.</summary>
    public void WakeForPath(Guid shareId, string path)
    {
        lock (_gate)
        {
            if (_fullReconcileRequested && !_pendingPaths.ContainsKey(shareId))
            {
                // A full walk is already queued and will cover this path anyway.
                return;
            }

            if (!_pendingPaths.TryGetValue(shareId, out var paths))
            {
                paths = new HashSet<string>(StringComparer.Ordinal);
                _pendingPaths[shareId] = paths;
            }

            paths.Add(path);
            if (paths.Count > MaxPendingPathsPerShare)
            {
                _pendingPaths.Remove(shareId);
                _fullReconcileRequested = true;
            }
        }

        _channel.Writer.TryWrite(true);
    }

    /// <summary>
    /// Waits for pending work, then takes everything accumulated so far. A timeout returns whatever had
    /// accumulated, which may be nothing; the caller owns the periodic full-reconcile schedule so that
    /// steady watcher traffic cannot keep resetting it.
    /// </summary>
    public async Task<AutomationWakeRequest> WaitAsync(
        TimeSpan maximumDelay,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(maximumDelay);

        try
        {
            await _channel.Reader.ReadAsync(timeout.Token);
            while (_channel.Reader.TryRead(out _)) { }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Periodic reconciliation timeout reached.
        }

        return Take();
    }

    private AutomationWakeRequest Take()
    {
        lock (_gate)
        {
            var targeted = new Dictionary<Guid, IReadOnlyCollection<string>>(_pendingPaths.Count);
            foreach (var (shareId, paths) in _pendingPaths)
            {
                targeted[shareId] = paths.ToArray();
            }

            var request = new AutomationWakeRequest(
                _fullReconcileRequested,
                targeted,
                _pendingBackfills.ToArray());
            _pendingPaths.Clear();
            _pendingBackfills.Clear();
            _fullReconcileRequested = false;
            return request;
        }
    }
}
