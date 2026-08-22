using MomentFerry.Web.Background;

namespace MomentFerry.Tests;

public sealed class AutomationWakeSignalTests
{
    [Fact]
    public async Task Wake_ReleasesPendingWaitAndRequestsFullReconcile()
    {
        var signal = new AutomationWakeSignal();
        signal.Wake();

        var request = await signal
            .WaitAsync(TimeSpan.FromHours(1), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(request.FullReconcile);
        Assert.Empty(request.TargetedPaths);
    }

    [Fact]
    public async Task WakeForPath_CarriesEveryCoalescedPath()
    {
        var signal = new AutomationWakeSignal();
        var shareId = Guid.NewGuid();

        // More wakes than the channel can hold: paths must accumulate rather than be dropped.
        signal.WakeForPath(shareId, "/shares/phone/a.jpg");
        signal.WakeForPath(shareId, "/shares/phone/b.jpg");
        signal.WakeForPath(shareId, "/shares/phone/c.jpg");

        var request = await signal
            .WaitAsync(TimeSpan.FromHours(1), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(request.FullReconcile);
        Assert.Equal(
            new[] { "/shares/phone/a.jpg", "/shares/phone/b.jpg", "/shares/phone/c.jpg" },
            request.TargetedPaths[shareId].OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public async Task WaitAsync_DrainsPendingWorkSoItIsNotReplayed()
    {
        var signal = new AutomationWakeSignal();
        var shareId = Guid.NewGuid();
        signal.WakeForPath(shareId, "/shares/phone/a.jpg");

        await signal.WaitAsync(TimeSpan.FromHours(1), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
        var second = await signal.WaitAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None);

        Assert.False(second.HasWork);
    }

    [Fact]
    public async Task WaitAsync_TimeoutReturnsNoWorkSoCallerOwnsTheSchedule()
    {
        var signal = new AutomationWakeSignal();

        var request = await signal.WaitAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None);

        Assert.False(request.HasWork);
    }

    [Fact]
    public async Task WakeForPath_DegradesToFullReconcileWhenPendingPathsOverflow()
    {
        var signal = new AutomationWakeSignal();
        var shareId = Guid.NewGuid();

        // A bulk import would otherwise hand every path to unbounded targeted evaluation.
        for (var index = 0; index < 1500; index++)
        {
            signal.WakeForPath(shareId, $"/shares/phone/shot-{index}.jpg");
        }

        var request = await signal
            .WaitAsync(TimeSpan.FromHours(1), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(request.FullReconcile);
        Assert.Empty(request.TargetedPaths);
    }

    [Fact]
    public async Task WakeForBackfill_CarriesEveryRequestedEvent()
    {
        var signal = new AutomationWakeSignal();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        signal.WakeForBackfill(first);
        signal.WakeForBackfill(second);
        signal.WakeForBackfill(first);

        var request = await signal
            .WaitAsync(TimeSpan.FromHours(1), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(request.FullReconcile);
        Assert.Equal(new[] { first, second }.OrderBy(x => x), request.BackfillEventIds.OrderBy(x => x));
    }

    [Fact]
    public async Task WakeForBackfill_IsDrainedAfterOnePass()
    {
        var signal = new AutomationWakeSignal();
        signal.WakeForBackfill(Guid.NewGuid());

        await signal.WaitAsync(TimeSpan.FromHours(1), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
        var second = await signal.WaitAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None);

        Assert.False(second.HasWork);
        Assert.Empty(second.BackfillEventIds);
    }

    [Fact]
    public async Task Wake_AndWakeForPath_AreBothReportedInOneBatch()
    {
        var signal = new AutomationWakeSignal();
        var shareId = Guid.NewGuid();

        signal.WakeForPath(shareId, "/shares/phone/a.jpg");
        signal.Wake();

        var request = await signal
            .WaitAsync(TimeSpan.FromHours(1), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(request.FullReconcile);
        Assert.Single(request.TargetedPaths[shareId]);
    }
}
