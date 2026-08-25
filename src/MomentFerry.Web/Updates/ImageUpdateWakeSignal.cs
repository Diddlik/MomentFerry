namespace MomentFerry.Web.Updates;

/// <summary>
/// Interrupts the update worker's wait. Without it, turning automatic updates on only took effect
/// when the running six-hour timer happened to tick, which looks exactly like a toggle that does
/// nothing. Coalescing is deliberate: several wakes before the worker runs are one check.
/// </summary>
public sealed class ImageUpdateWakeSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Wake()
    {
        if (_signal.CurrentCount == 0)
        {
            try { _signal.Release(); } catch (SemaphoreFullException) { }
        }
    }

    /// <summary>Waits for a wake-up or for the period to elapse, whichever comes first.</summary>
    public Task WaitAsync(TimeSpan period, CancellationToken cancellationToken) =>
        _signal.WaitAsync(period, cancellationToken);
}
