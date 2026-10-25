using MomentFerry.Application.Abstractions;
using MomentFerry.Application.Services;
using MomentFerry.Core.Domain;
using MomentFerry.Web.Background;

namespace MomentFerry.Web.Api;

public static class RoutingEndpoints
{
    public static IEndpointRouteBuilder MapRoutingEndpoints(this IEndpointRouteBuilder app)
    {
        // A preview outlasts what a reverse proxy waits for, so it runs as a job: this starts it (or
        // returns the one already running for the share) and the job endpoint reports progress and,
        // once finished, the result.
        app.MapPost("/api/v1/shares/{id:guid}/routing-preview", async (
            Guid id,
            IShareRepository shares,
            RoutingPreviewJobs jobs,
            CancellationToken ct) =>
        {
            var share = await shares.GetAsync(id, ct);
            if (share is null) return Results.NotFound();
            if (!share.Enabled || share.Role == ShareRole.Destination)
            {
                return Results.BadRequest(new { error = "Routing preview requires an enabled source share." });
            }

            var job = jobs.Start(share);
            return Results.Accepted($"/api/v1/routing-preview-jobs/{job.Id}", job);
        });

        app.MapGet("/api/v1/routing-preview-jobs/{id:guid}", (Guid id, RoutingPreviewJobs jobs) =>
            jobs.Find(id) is { } job ? Results.Ok(job) : Results.NotFound());

        app.MapGet("/api/v1/media-files", async (
            int? limit,
            IMediaFileRepository repository,
            CancellationToken ct) =>
            Results.Ok(await repository.ListRecentAsync(Math.Clamp(limit ?? 200, 1, 2000), ct)));

        return app;
    }
}
