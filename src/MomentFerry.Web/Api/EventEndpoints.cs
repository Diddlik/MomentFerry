using MomentFerry.Application.Abstractions;
using MomentFerry.Application.Services;
using MomentFerry.Core.Domain;
using MomentFerry.Web.Background;

namespace MomentFerry.Web.Api;

public static class EventEndpoints
{
    public static IEndpointRouteBuilder MapEventEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/events");

        group.MapGet("/", async (IMediaEventRepository repository, CancellationToken ct) =>
            Results.Ok(await repository.ListAsync(ct)));

        group.MapGet("/{id:guid}", async (Guid id, IMediaEventRepository repository, CancellationToken ct) =>
        {
            var mediaEvent = await repository.GetAsync(id, ct);
            return mediaEvent is null ? Results.NotFound() : Results.Ok(mediaEvent);
        });

        group.MapPost("/", async (
            EventRequest request,
            IMediaEventRepository repository,
            ISourceGroupRepository sourceGroups,
            IShareRepository shares,
            EventControlService control,
            AutomationWakeSignal wakeSignal,
            CancellationToken ct) =>
        {
            var validation = await ValidateAsync(request, sourceGroups, shares, ct);
            if (validation is not null) return validation;

            var mediaEvent = ToDomain(Guid.NewGuid(), request);
            await repository.UpsertAsync(mediaEvent, ct);
            await RequeueAndWakeAsync(control, wakeSignal, ct, mediaEvent);
            return Results.Created($"/api/v1/events/{mediaEvent.Id}", mediaEvent);
        });

        group.MapPut("/{id:guid}", async (
            Guid id,
            EventRequest request,
            IMediaEventRepository repository,
            ISourceGroupRepository sourceGroups,
            IShareRepository shares,
            EventControlService control,
            AutomationWakeSignal wakeSignal,
            CancellationToken ct) =>
        {
            if (await repository.GetAsync(id, ct) is not { } previous) return Results.NotFound();
            var validation = await ValidateAsync(request, sourceGroups, shares, ct);
            if (validation is not null) return validation;

            var mediaEvent = ToDomain(id, request);
            await repository.UpsertAsync(mediaEvent, ct);
            // Both windows are requeued: files leaving the old window must stop matching it.
            await RequeueAndWakeAsync(control, wakeSignal, ct, previous, mediaEvent);
            return Results.Ok(mediaEvent);
        });

        group.MapPost("/{id:guid}/backfill", async (
            Guid id,
            IMediaEventRepository repository,
            IRuntimeSettingsStore settings,
            AutomationStatus automationStatus,
            AutomationWakeSignal wakeSignal,
            IClock clock,
            CancellationToken ct) =>
        {
            var mediaEvent = await repository.GetAsync(id, ct);
            if (mediaEvent is null) return Results.NotFound();

            var runtime = await settings.GetAsync(ct);
            if (!runtime.AutomationEnabled)
                return Results.Conflict(new { error = "Automation is disabled." });
            if (automationStatus.Snapshot().CycleRunning)
                return Results.Conflict(new { error = "An automation cycle is already running." });

            var requestedAt = clock.UtcNow;
            wakeSignal.WakeForBackfill(id);
            return Results.Accepted(value: new { requestedAt, eventId = id, eventName = mediaEvent.Name });
        });

        // Route again for a whole event. The backfill alone cannot help here: it lifts the per-cycle
        // file cap, but every file still passes the terminal-state check, so media that already
        // completed once stays put no matter how often the walk runs.
        group.MapPost("/{id:guid}/route-again", async (
            Guid id,
            IMediaEventRepository repository,
            IMediaOperationRepository operations,
            IRuntimeSettingsStore settings,
            AutomationStatus automationStatus,
            AutomationWakeSignal wakeSignal,
            IClock clock,
            CancellationToken ct) =>
        {
            var mediaEvent = await repository.GetAsync(id, ct);
            if (mediaEvent is null) return Results.NotFound();

            var runtime = await settings.GetAsync(ct);
            if (runtime.DryRun)
                return Results.Conflict(new { error = "MomentFerry is in Dry Run mode. Disable Dry Run in Settings first." });
            if (!runtime.AutomationEnabled)
                return Results.Conflict(new { error = "Automation is disabled." });
            if (automationStatus.Snapshot().CycleRunning)
                return Results.Conflict(new { error = "An automation cycle is already running." });

            var requestedAt = clock.UtcNow;
            var superseded = await operations.SupersedeTerminalByEventAsync(
                id,
                "Superseded by an explicit route-again request for the whole event.",
                requestedAt,
                ct);
            wakeSignal.WakeForBackfill(id);

            return Results.Accepted(value: new
            {
                requestedAt,
                eventId = id,
                eventName = mediaEvent.Name,
                superseded
            });
        });

        group.MapPost("/{id:guid}/start", async (
            Guid id,
            EventControlService control,
            CancellationToken ct) =>
            ToResult(await control.StartAsync(id, ct)));

        group.MapPost("/{id:guid}/stop", async (
            Guid id,
            EventControlService control,
            CancellationToken ct) =>
            ToResult(await control.StopAsync(id, ct)));

        group.MapPost("/quick-start", async (
            QuickEventStartRequest request,
            EventControlService control,
            CancellationToken ct) =>
            ToResult(await control.QuickStartAsync(new QuickStartEventCommand(
                request.Name,
                request.SourceGroupId,
                request.DestinationShareId,
                request.Type,
                request.DestinationFolderTemplate,
                request.OperationMode,
                request.ConflictStrategy,
                request.DuplicateStrategy), ct)));

        group.MapPost("/quick-stop", async (
            QuickEventStopRequest request,
            EventControlService control,
            CancellationToken ct) =>
            ToResult(await control.QuickStopAsync(request.Name, ct)));

        group.MapDelete("/{id:guid}", async (
            Guid id,
            IMediaEventRepository repository,
            EventControlService control,
            AutomationWakeSignal wakeSignal,
            CancellationToken ct) =>
        {
            if (await repository.GetAsync(id, ct) is not { } existing) return Results.NotFound();
            if (!await repository.DeleteAsync(id, ct)) return Results.NotFound();

            await RequeueAndWakeAsync(control, wakeSignal, ct, existing);
            return Results.NoContent();
        });

        return app;
    }

    private static async Task RequeueAndWakeAsync(
        EventControlService control,
        AutomationWakeSignal wakeSignal,
        CancellationToken cancellationToken,
        params MediaEvent[] affected)
    {
        foreach (var mediaEvent in affected)
        {
            await control.RequeueAffectedMediaAsync(mediaEvent, cancellationToken);
        }

        wakeSignal.Wake();
    }

    private static IResult ToResult(EventControlResult result) => result.Status switch
    {
        EventControlStatus.Success => Results.Ok(result.Event),
        EventControlStatus.Created => Results.Created($"/api/v1/events/{result.Event!.Id}", result.Event),
        EventControlStatus.NotFound => Results.NotFound(new { error = result.Error }),
        EventControlStatus.Conflict => Results.Conflict(new { error = result.Error }),
        EventControlStatus.Invalid => Results.BadRequest(new { error = result.Error }),
        _ => Results.Problem("Unknown event control result.")
    };

    private static async Task<IResult?> ValidateAsync(
        EventRequest request,
        ISourceGroupRepository sourceGroups,
        IShareRepository shares,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return Results.BadRequest(new { error = "Name is required." });
        if (request.EndAt is not null && request.EndAt.Value < request.StartAt)
            return Results.BadRequest(new { error = "EndAt must not be before StartAt." });
        if (string.IsNullOrWhiteSpace(request.DestinationFolderTemplate) || Path.IsPathRooted(request.DestinationFolderTemplate))
            return Results.BadRequest(new { error = "DestinationFolderTemplate must be a relative folder template." });

        var sourceGroup = await sourceGroups.GetAsync(request.SourceGroupId, cancellationToken);
        if (sourceGroup is null)
            return Results.BadRequest(new { error = "Source group does not exist." });

        var destination = await shares.GetAsync(request.DestinationShareId, cancellationToken);
        if (destination is null || !destination.Enabled || destination.Role == ShareRole.Source)
            return Results.BadRequest(new { error = "Destination share must exist, be enabled, and support destination writes." });

        if (sourceGroup.ShareIds.Contains(request.DestinationShareId))
            return Results.BadRequest(new { error = "Destination share cannot also be a source of the same event. This prevents routing/sync loops." });

        return null;
    }

    private static MediaEvent ToDomain(Guid id, EventRequest request) => new()
    {
        Id = id,
        Name = request.Name.Trim(),
        Type = string.IsNullOrWhiteSpace(request.Type) ? null : request.Type.Trim(),
        StartAt = request.StartAt,
        EndAt = request.EndAt,
        Status = request.Status,
        SourceGroupId = request.SourceGroupId,
        DestinationShareId = request.DestinationShareId,
        DestinationFolderTemplate = request.DestinationFolderTemplate.Trim(),
        OperationMode = request.OperationMode,
        ConflictStrategy = request.ConflictStrategy,
        DuplicateStrategy = request.DuplicateStrategy
    };
}

public sealed record EventRequest(
    string Name,
    string? Type,
    DateTimeOffset StartAt,
    DateTimeOffset? EndAt,
    MediaEventStatus Status,
    Guid SourceGroupId,
    Guid DestinationShareId,
    string DestinationFolderTemplate = "{event.name}",
    OperationMode OperationMode = OperationMode.SafeMove,
    ConflictStrategy ConflictStrategy = ConflictStrategy.AppendSourceName,
    DuplicateStrategy DuplicateStrategy = DuplicateStrategy.SafeMoveToExisting);

public sealed record QuickEventStartRequest(
    string Name,
    Guid SourceGroupId,
    Guid DestinationShareId,
    string? Type = "Vacation",
    string DestinationFolderTemplate = "{event.name}",
    OperationMode OperationMode = OperationMode.SafeMove,
    ConflictStrategy ConflictStrategy = ConflictStrategy.AppendSourceName,
    DuplicateStrategy DuplicateStrategy = DuplicateStrategy.SafeMoveToExisting);

public sealed record QuickEventStopRequest(string Name);
