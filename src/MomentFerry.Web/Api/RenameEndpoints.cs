using MomentFerry.Application.Abstractions;
using MomentFerry.Application.Services;
using MomentFerry.Core.Domain;

namespace MomentFerry.Web.Api;

public static class RenameEndpoints
{
    public static IEndpointRouteBuilder MapRenameEndpoints(this IEndpointRouteBuilder app)
    {
        var presets = app.MapGroup("/api/v1/rename-presets");

        presets.MapGet("/", async (IRenamePresetRepository repository, CancellationToken ct) =>
            Results.Ok(await repository.ListAsync(ct)));

        presets.MapPost("/", async (
            RenamePresetRequest request,
            IRenamePresetRepository repository,
            CancellationToken ct) =>
        {
            if (Validate(request) is { } invalid) return invalid;
            var preset = new RenamePreset { Id = Guid.NewGuid(), Name = request.Name.Trim(), Template = request.Template.Trim() };
            await repository.UpsertAsync(preset, ct);
            return Results.Created($"/api/v1/rename-presets/{preset.Id}", preset);
        });

        presets.MapPut("/{id:guid}", async (
            Guid id,
            RenamePresetRequest request,
            IRenamePresetRepository repository,
            CancellationToken ct) =>
        {
            if (await repository.GetAsync(id, ct) is null) return Results.NotFound();
            if (Validate(request) is { } invalid) return invalid;
            var preset = new RenamePreset { Id = id, Name = request.Name.Trim(), Template = request.Template.Trim() };
            await repository.UpsertAsync(preset, ct);
            return Results.Ok(preset);
        });

        presets.MapDelete("/{id:guid}", async (Guid id, IRenamePresetRepository repository, CancellationToken ct) =>
            await repository.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound());

        // Renders a template against real indexed media so a template can be checked before it is
        // attached to a share and starts naming files.
        presets.MapPost("/preview", async (
            RenamePreviewRequest request,
            IMediaFileRepository mediaFiles,
            IShareRepository shares,
            ICameraMappingRepository cameraMappings,
            ShareDiscoveryService discovery,
            CancellationToken ct) =>
        {
            var cameraNames = FileNameTemplate.BuildCameraNames(await cameraMappings.ListAsync(ct));
            var allShares = await shares.ListAsync(ct);
            var shareNames = allShares.ToDictionary(x => x.Id, x => x);
            var results = new List<object>();

            // Indexed media is preferred because it carries the capture time and camera the tokens need.
            foreach (var sample in (await mediaFiles.ListRecentAsync(25, ct)).Take(4))
            {
                shareNames.TryGetValue(sample.SourceShareId, out var share);
                results.Add(RenderSample(
                    request,
                    Path.GetFileNameWithoutExtension(sample.OriginalName),
                    Path.GetExtension(sample.OriginalName),
                    sample.CapturedAt ?? DateTimeOffset.UtcNow,
                    sample.CameraMake,
                    sample.CameraModel,
                    share?.Name ?? "Source",
                    share?.Owner,
                    cameraNames,
                    share?.Name ?? "indexed",
                    results.Count + 1));
            }

            // Nothing indexed yet, so read real filenames straight off the source shares. These have no
            // capture metadata behind them, so the file's last-write time stands in for the capture time.
            if (results.Count == 0)
            {
                foreach (var share in allShares.Where(x => x.Enabled && x.Role is ShareRole.Source or ShareRole.Both))
                {
                    foreach (var file in discovery.Scan(share, 4))
                    {
                        results.Add(RenderSample(
                            request,
                            Path.GetFileNameWithoutExtension(file.FullPath),
                            Path.GetExtension(file.FullPath),
                            file.LastWriteUtc,
                            null,
                            null,
                            share.Name,
                            share.Owner,
                            cameraNames,
                            share.Name + " (not indexed yet)",
                            results.Count + 1));
                        if (results.Count >= 4) break;
                    }

                    if (results.Count >= 4) break;
                }
            }

            if (results.Count == 0)
            {
                // No shares and no media: a worked example still shows the shape of the result.
                results.Add(RenderSample(
                    request,
                    "img20260216_123056",
                    ".jpg",
                    new DateTimeOffset(2026, 2, 16, 12, 30, 55, TimeSpan.Zero),
                    "OnePlus",
                    "CPH2581",
                    "Phone",
                    "Pavel",
                    cameraNames,
                    "example",
                    1));
            }

            return Results.Ok(new { samples = results });
        });

        var mappings = app.MapGroup("/api/v1/camera-mappings");

        mappings.MapGet("/", async (ICameraMappingRepository repository, CancellationToken ct) =>
            Results.Ok(await repository.ListAsync(ct)));

        mappings.MapPost("/", async (
            CameraMappingRequest request,
            ICameraMappingRepository repository,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.From) || string.IsNullOrWhiteSpace(request.To))
                return Results.BadRequest(new { error = "Both the reported value and the replacement are required." });

            var mapping = new CameraMapping
            {
                Id = request.Id ?? Guid.NewGuid(),
                From = request.From.Trim(),
                To = request.To.Trim()
            };
            await repository.UpsertAsync(mapping, ct);
            return Results.Ok(mapping);
        });

        mappings.MapDelete("/{id:guid}", async (Guid id, ICameraMappingRepository repository, CancellationToken ct) =>
            await repository.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound());

        return app;
    }

    private static object RenderSample(
        RenamePreviewRequest request,
        string stem,
        string extension,
        DateTimeOffset capturedAt,
        string? cameraMake,
        string? cameraModel,
        string sourceName,
        string? owner,
        IReadOnlyDictionary<string, string> cameraNames,
        string origin,
        int sequence)
    {
        var context = new FileNameContext(
            stem,
            capturedAt,
            FileNameTemplate.ResolveCamera(cameraMake, cameraModel, cameraNames),
            cameraMake,
            cameraModel,
            sourceName,
            owner,
            request.EventName ?? "Italy 2026",
            request.EventType ?? "Vacation");

        var renamed = context.Stem;
        if (!string.IsNullOrWhiteSpace(request.SourceTemplate))
        {
            renamed = FileNameTemplate.Render(request.SourceTemplate, context, sequence);
        }

        if (!string.IsNullOrWhiteSpace(request.DestinationTemplate))
        {
            renamed = FileNameTemplate.Render(request.DestinationTemplate, context with { Stem = renamed }, sequence);
        }

        return new
        {
            original = stem + extension,
            result = renamed + extension,
            camera = context.Camera,
            origin
        };
    }

    private static IResult? Validate(RenamePresetRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return Results.BadRequest(new { error = "Name is required." });
        if (string.IsNullOrWhiteSpace(request.Template))
            return Results.BadRequest(new { error = "Template is required." });
        if (request.Template.Contains('/') || request.Template.Contains('\\'))
            return Results.BadRequest(new { error = "A rename template names a file and cannot contain path separators." });
        return null;
    }
}

public sealed record RenamePresetRequest(string Name, string Template);

public sealed record CameraMappingRequest(string From, string To, Guid? Id = null);

public sealed record RenamePreviewRequest(
    string? SourceTemplate,
    string? DestinationTemplate,
    string? EventName = null,
    string? EventType = null);
