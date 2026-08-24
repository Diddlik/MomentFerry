using MomentFerry.Application.Abstractions;
using MomentFerry.Application.Services;
using MomentFerry.Core.Domain;

namespace MomentFerry.Tests;

public sealed class EventControlServiceTests
{
    [Fact]
    public async Task QuickStart_IsIdempotentForSameActiveConfiguration()
    {
        var fixture = CreateFixture();
        var command = new QuickStartEventCommand(
            "Croatia 2026",
            fixture.Group.Id,
            fixture.Destination.Id);

        var first = await fixture.Service.QuickStartAsync(command);
        var second = await fixture.Service.QuickStartAsync(command);

        Assert.Equal(EventControlStatus.Created, first.Status);
        Assert.Equal(EventControlStatus.Success, second.Status);
        Assert.NotNull(first.Event);
        Assert.Equal(first.Event!.Id, second.Event!.Id);
        Assert.Single(await fixture.Events.ListAsync());
    }

    [Fact]
    public async Task Start_DoesNotReopenClosedEvent()
    {
        var fixture = CreateFixture();
        var closed = new MediaEvent
        {
            Name = "Finished trip",
            StartAt = fixture.Clock.UtcNow.AddDays(-5),
            EndAt = fixture.Clock.UtcNow.AddDays(-1),
            Status = MediaEventStatus.Closed,
            SourceGroupId = fixture.Group.Id,
            DestinationShareId = fixture.Destination.Id
        };
        await fixture.Events.UpsertAsync(closed);

        var result = await fixture.Service.StartAsync(closed.Id);
        var persisted = await fixture.Events.GetAsync(closed.Id);

        Assert.Equal(EventControlStatus.Conflict, result.Status);
        Assert.Equal(MediaEventStatus.Closed, persisted!.Status);
        Assert.Equal(closed.StartAt, persisted.StartAt);
        Assert.Equal(closed.EndAt, persisted.EndAt);
    }

    [Fact]
    public async Task QuickStart_RejectsDestinationThatIsAlsoSourceGroupMember()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero));
        var both = new Share
        {
            Name = "Loop share",
            Path = "/shares/both",
            Role = ShareRole.Both
        };
        var shares = new MemoryShareRepository([both]);
        var group = new SourceGroup { Name = "Loop", ShareIds = [both.Id] };
        var groups = new MemorySourceGroupRepository([group]);
        var events = new MemoryEventRepository();
        var service = new EventControlService(events, groups, shares, new MemoryMediaFileRepository(), clock);

        var result = await service.QuickStartAsync(new QuickStartEventCommand(
            "Loop event",
            group.Id,
            both.Id));

        Assert.Equal(EventControlStatus.Invalid, result.Status);
        Assert.Empty(await events.ListAsync());
    }

    private static Fixture CreateFixture()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero));
        var source = new Share
        {
            Name = "Phone",
            Path = "/sources/phone",
            Role = ShareRole.Source
        };
        var destination = new Share
        {
            Name = "Family",
            Path = "/destinations/family",
            Role = ShareRole.Destination
        };
        var shares = new MemoryShareRepository([source, destination]);
        var group = new SourceGroup
        {
            Name = "Family phones",
            ShareIds = [source.Id]
        };
        var groups = new MemorySourceGroupRepository([group]);
        var events = new MemoryEventRepository();
        var service = new EventControlService(events, groups, shares, new MemoryMediaFileRepository(), clock);
        return new Fixture(service, events, group, destination, clock);
    }

    private sealed record Fixture(
        EventControlService Service,
        MemoryEventRepository Events,
        SourceGroup Group,
        Share Destination,
        TestClock Clock);

    private sealed class TestClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    /// <summary>Records requeue windows so lifecycle tests can assert the re-match trigger fired.</summary>
    private sealed class MemoryMediaFileRepository : IMediaFileRepository
    {
        public List<(IReadOnlyCollection<Guid> ShareIds, DateTimeOffset StartAt, DateTimeOffset? EndAt)> Requeues { get; } = [];

        public Task<IReadOnlyList<MediaFile>> ListRecentAsync(int limit = 200, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<MediaFile>>([]);

        public Task<IReadOnlyList<MediaFile>> ListBySourceAsync(Guid sourceShareId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<MediaFile>>([]);

        public Task<MediaFile?> GetAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult<MediaFile?>(null);

        public Task<MediaFile?> GetBySourceAsync(Guid sourceShareId, string sourcePath, CancellationToken cancellationToken = default)
            => Task.FromResult<MediaFile?>(null);

        public Task<int> ClearMetadataStampAsync(Guid? shareId, CancellationToken cancellationToken = default)
            => Task.FromResult(0);

        public Task<int> DeleteUnreferencedAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
            => Task.FromResult(0);

        public Task UpsertAsync(MediaFile mediaFile, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<int> RequeueByCaptureWindowAsync(
            IReadOnlyCollection<Guid> sourceShareIds,
            DateTimeOffset startAt,
            DateTimeOffset? endAt,
            CancellationToken cancellationToken = default)
        {
            Requeues.Add((sourceShareIds, startAt, endAt));
            return Task.FromResult(0);
        }
    }

    private sealed class MemoryEventRepository : IMediaEventRepository
    {
        private readonly Dictionary<Guid, MediaEvent> _items = [];

        public Task<IReadOnlyList<MediaEvent>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MediaEvent>>(_items.Values.ToArray());

        public Task<MediaEvent?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_items.GetValueOrDefault(id));

        public Task<IReadOnlyList<MediaEvent>> ListMatchableAsync(
            DateTimeOffset capturedAt,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MediaEvent>>(_items.Values
                .Where(x => x.Status is MediaEventStatus.Active or MediaEventStatus.Closed &&
                            x.StartAt <= capturedAt &&
                            (x.EndAt is null || capturedAt <= x.EndAt.Value))
                .ToArray());

        public Task UpsertAsync(MediaEvent mediaEvent, CancellationToken cancellationToken = default)
        {
            _items[mediaEvent.Id] = mediaEvent;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_items.Remove(id));
    }

    private sealed class MemorySourceGroupRepository(IEnumerable<SourceGroup> initial) : ISourceGroupRepository
    {
        private readonly Dictionary<Guid, SourceGroup> _items = initial.ToDictionary(x => x.Id);

        public Task<IReadOnlyList<SourceGroup>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SourceGroup>>(_items.Values.ToArray());

        public Task<SourceGroup?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_items.GetValueOrDefault(id));

        public Task UpsertAsync(SourceGroup sourceGroup, CancellationToken cancellationToken = default)
        {
            _items[sourceGroup.Id] = sourceGroup;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_items.Remove(id));
    }

    private sealed class MemoryShareRepository(IEnumerable<Share> initial) : IShareRepository
    {
        private readonly Dictionary<Guid, Share> _items = initial.ToDictionary(x => x.Id);

        public Task<IReadOnlyList<Share>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Share>>(_items.Values.ToArray());

        public Task<Share?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_items.GetValueOrDefault(id));

        public Task UpsertAsync(Share share, CancellationToken cancellationToken = default)
        {
            _items[share.Id] = share;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_items.Remove(id));
    }
}
