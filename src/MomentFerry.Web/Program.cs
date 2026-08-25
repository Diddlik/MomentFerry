using System.Reflection;
using System.Text.Json.Serialization;
using MomentFerry.Application.Abstractions;
using MomentFerry.Application.Services;
using MomentFerry.Core.Domain;
using MomentFerry.Infrastructure;
using MomentFerry.Infrastructure.Metadata;
using MomentFerry.Infrastructure.Persistence;
using MomentFerry.Infrastructure.Runtime;
using MomentFerry.Web.Api;
using MomentFerry.Web.Background;
using MomentFerry.Web.Diagnostics;
using MomentFerry.Web.Integrations;
using MomentFerry.Web.Updates;

var builder = WebApplication.CreateBuilder(args);

var activityLog = new ActivityLog(
    Math.Clamp(builder.Configuration.GetValue("MomentFerry:ActivityLog:Capacity", 500), 50, 5000));
builder.Services.AddSingleton(activityLog);
builder.Logging.AddProvider(new ActivityLogProvider(activityLog));

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();
builder.Services.AddHttpClient();

builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IFileSystemGateway, LocalFileSystemGateway>();
builder.Services.AddSingleton<IHashService, Sha256HashService>();
builder.Services.AddSingleton<ShareDiscoveryService>();
builder.Services.AddSingleton<IMediaMetadataExtractor, ExifToolMetadataExtractor>();
builder.Services.AddSingleton<MetadataPreviewService>();
builder.Services.AddSingleton<DestinationPathResolver>();
builder.Services.AddSingleton<RenameContextFactory>();
builder.Services.AddSingleton<RoutingPreviewService>();
builder.Services.AddSingleton<SafeTransferService>();
builder.Services.AddSingleton<TransferCoordinator>();
builder.Services.AddSingleton<OperationRecoveryService>();
builder.Services.AddSingleton<EventControlService>();
builder.Services.AddSingleton<QuarantineService>();
builder.Services.AddSingleton<RoutedFileRenameService>();
var automationStatusPath = builder.Configuration["MomentFerry:Automation:StatusPath"] ?? "data/automation-status.json";
builder.Services.AddSingleton(sp => new AutomationStatus(
    automationStatusPath,
    sp.GetRequiredService<ILogger<AutomationStatus>>()));
builder.Services.AddSingleton<AutomationWakeSignal>();

var runtimeDefaults = new MomentFerryRuntimeSettings(
    builder.Configuration.GetValue("MomentFerry:DryRun", true),
    builder.Configuration.GetValue("MomentFerry:Automation:Enabled", true),
    builder.Configuration.GetValue("MomentFerry:ReconciliationIntervalSeconds", 1800),
    builder.Configuration.GetValue("MomentFerry:Automation:MaxFilesPerSharePerCycle", 200),
    builder.Configuration.GetValue("MomentFerry:Automation:MaxParallelMetadataReads", 2),
    builder.Configuration.GetValue("MomentFerry:Automation:AllowFilesystemTimestampFallback", false),
    builder.Configuration.GetValue("MomentFerry:MinimumFreeSpaceReserveBytes", LocalFileSystemGateway.DefaultMinimumFreeSpaceReserveBytes),
    builder.Configuration.GetValue("MomentFerry:Updates:Automatic", false),
    builder.Configuration.GetValue("MomentFerry:OperationRetentionDays", 0));
var runtimeSettingsPath = builder.Configuration["MomentFerry:RuntimeSettingsPath"] ?? "data/runtime-settings.json";
builder.Services.AddSingleton<IRuntimeSettingsStore>(
    new JsonRuntimeSettingsStore(runtimeSettingsPath, runtimeDefaults));
builder.Services.AddHostedService<SourceShareWatcherWorker>();
builder.Services.AddHostedService<MediaRoutingWorker>();
builder.Services.AddHostedService<MqttIntegrationWorker>();
// InformationalVersion keeps the prerelease suffix ("0.0.0-dev.42") that AssemblyVersion drops.
var runningVersion = builder.Configuration["MomentFerry:Updates:CurrentVersion"]
    ?? typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion.Split('+')[0]
    ?? typeof(Program).Assembly.GetName().Version?.ToString(3)
    ?? "0.0.0";
builder.Services.AddSingleton(new ImageUpdateOptions(
    builder.Configuration["MomentFerry:Updates:ReleaseApiUrl"] ?? "https://api.github.com/repos/diddlik/MomentFerry/releases/latest",
    builder.Configuration["MomentFerry:Updates:WatchtowerUrl"],
    builder.Configuration["MomentFerry:Updates:WatchtowerToken"],
    runningVersion));
builder.Services.AddSingleton<IImageUpdateStatusStore>(new JsonImageUpdateStatusStore(
    builder.Configuration["MomentFerry:Updates:StatusPath"] ?? "data/update-status.json"));
builder.Services.AddSingleton<ImageUpdateService>();
builder.Services.AddSingleton<ImageUpdateWakeSignal>();
builder.Services.AddHostedService<ImageUpdateWorker>();

var databasePath = builder.Configuration["MomentFerry:Database:Path"] ?? "data/momentferry.db";
var sourceRoots = builder.Configuration.GetSection("MomentFerry:SourceRoots").Get<string[]>() ?? ["/sources"];
var destinationRoots = builder.Configuration.GetSection("MomentFerry:DestinationRoots").Get<string[]>() ?? ["/destinations"];
var allowedRoots = builder.Configuration.GetSection("MomentFerry:AllowedRoots").Get<string[]>()
    ?? sourceRoots.Concat(destinationRoots).Distinct().ToArray();

builder.Services.AddSingleton(new SqliteConnectionFactory(databasePath));
builder.Services.AddSingleton<IDatabaseInitializer, SqliteDatabaseInitializer>();
builder.Services.AddSingleton<IShareRepository, SqliteShareRepository>();
builder.Services.AddSingleton<ISourceGroupRepository, SqliteSourceGroupRepository>();
builder.Services.AddSingleton<IMediaEventRepository, SqliteMediaEventRepository>();
builder.Services.AddSingleton<IRenamePresetRepository, SqliteRenamePresetRepository>();
builder.Services.AddSingleton<ICameraMappingRepository, SqliteCameraMappingRepository>();
builder.Services.AddSingleton<IMediaFileRepository, SqliteMediaFileRepository>();
builder.Services.AddSingleton<IMediaOperationRepository, SqliteMediaOperationRepository>();

var app = builder.Build();

await app.Services.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
var recoveryReport = await app.Services.GetRequiredService<OperationRecoveryService>().RecoverAsync();
if (recoveryReport.Total > 0)
{
    app.Logger.LogInformation(
        "MomentFerry recovery processed {Total} operations: {Completed} completed, {Quarantined} quarantined, {RetryPending} retry pending",
        recoveryReport.Total,
        recoveryReport.Completed,
        recoveryReport.Quarantined,
        recoveryReport.RetryPending);

    // Counts alone leave an operator guessing which files are stuck and why, and these states are only
    // cleared by an explicit retry, so name each one.
    foreach (var item in recoveryReport.Items.Where(x =>
        x.State is not (MediaOperationState.Completed or MediaOperationState.Ignored)))
    {
        app.Logger.LogWarning(
            "MomentFerry recovery left operation {OperationId} in {State}: {Reason}",
            item.OperationId,
            item.State,
            item.Message ?? "no reason recorded");
    }
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapOpenApi();
app.UseSwaggerUI(options =>
{
    options.RoutePrefix = "docs";
    options.SwaggerEndpoint("/openapi/v1.json", "MomentFerry API v1");
});

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "MomentFerry"
}));

app.MapGet("/api/v1/info", async (
    IClock clock,
    IRuntimeSettingsStore settingsStore,
    CancellationToken ct) =>
{
    var settings = await settingsStore.GetAsync(ct);
    return Results.Ok(new
    {
        name = "MomentFerry",
        status = "automation",
        utcNow = clock.UtcNow,
        databaseSchemaVersion = SqliteDatabaseInitializer.CurrentSchemaVersion,
        openApiDocument = "/openapi/v1.json",
        apiDocs = "/api-docs.html",
        settings.DryRun,
        settings.AutomationEnabled,
        settings.ReconciliationIntervalSeconds,
        settings.MaxFilesPerSharePerCycle,
        settings.AllowFilesystemTimestampFallback,
        settings.MinimumFreeSpaceReserveBytes,
        filesystemWatcherEnabled = true,
        mqttEnabled = builder.Configuration.GetValue("MomentFerry:Mqtt:Enabled", false),
        allowedRoots
    });
});

app.MapGet("/api/v1/share-presets", () => Results.Ok(SharePresets.All));

app.MapGet("/api/v1/folders", (ShareRole role, string? path) =>
{
    var roots = role switch
    {
        ShareRole.Source => sourceRoots,
        ShareRole.Destination => destinationRoots,
        _ => sourceRoots.Concat(destinationRoots).Distinct().ToArray()
    };

    try
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            return Results.Ok(new
            {
                path = Path.GetFullPath(path),
                folders = FolderBrowser.ListChildren(path, roots)
            });
        }

        return Results.Ok(new
        {
            roots = roots
                .Where(Directory.Exists)
                .Select(root => new
                {
                    name = Path.GetFileName(Path.TrimEndingDirectorySeparator(root)),
                    path = Path.GetFullPath(root),
                    folders = FolderBrowser.ListChildren(root, roots)
                })
                .ToArray()
        });
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        return Results.Problem(
            title: "Folder browsing failed",
            detail: exception.Message,
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/api/v1/shares", async (IShareRepository repository, CancellationToken ct) =>
    Results.Ok(await repository.ListAsync(ct)));

app.MapGet("/api/v1/shares/{id:guid}", async (Guid id, IShareRepository repository, CancellationToken ct) =>
{
    var share = await repository.GetAsync(id, ct);
    return share is null ? Results.NotFound() : Results.Ok(share);
});

app.MapGet("/api/v1/shares/{id:guid}/probe", async (
    Guid id,
    IShareRepository repository,
    IFileSystemGateway fileSystem,
    CancellationToken ct) =>
{
    var share = await repository.GetAsync(id, ct);
    if (share is null) return Results.NotFound();

    var exists = fileSystem.DirectoryExists(share.Path);
    var readable = false;
    string? error = null;

    if (exists)
    {
        try
        {
            _ = fileSystem.EnumerateFiles(share.Path, false).Take(1).ToArray();
            readable = true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            error = ex.Message;
        }
    }

    return Results.Ok(new
    {
        share.Id,
        share.Path,
        exists,
        readable,
        pathAllowed = IsPathAllowed(share.Path, allowedRoots),
        error
    });
});

app.MapGet("/api/v1/shares/{id:guid}/scan", async (
    Guid id,
    int? limit,
    IShareRepository repository,
    ShareDiscoveryService discovery,
    CancellationToken ct) =>
{
    var share = await repository.GetAsync(id, ct);
    if (share is null) return Results.NotFound();

    try
    {
        var sampleSize = Math.Clamp(limit ?? 200, 1, 2000);
        var files = new List<DiscoveredFile>(sampleSize);
        var total = 0;
        var stable = 0;

        // Count the whole share, but only return the first sampleSize files.
        foreach (var file in discovery.Enumerate(share))
        {
            total++;
            if (file.State == DiscoveryState.Stable) stable++;
            if (files.Count < sampleSize) files.Add(file);
        }

        return Results.Ok(new
        {
            share.Id,
            share.Name,
            share.Path,
            total,
            stable,
            waitingStable = total - stable,
            sampled = files.Count,
            files
        });
    }
    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
    {
        return Results.Problem(
            title: "Share scan failed",
            detail: ex.Message,
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/api/v1/shares/{id:guid}/metadata-preview", async (
    Guid id,
    int? limit,
    IShareRepository repository,
    MetadataPreviewService previewService,
    IRuntimeSettingsStore runtimeSettings,
    CancellationToken ct) =>
{
    var share = await repository.GetAsync(id, ct);
    if (share is null) return Results.NotFound();
    if (share.Role == ShareRole.Destination)
        return Results.BadRequest(new { error = "Metadata preview is only available for source shares." });

    try
    {
        var settings = await runtimeSettings.GetAsync(ct);
        var preview = await previewService.PreviewAsync(
            share,
            Math.Clamp(limit ?? 10, 1, 50),
            ct,
            settings.MaxParallelMetadataReads);
        return Results.Ok(new { share.Id, share.Name, total = preview.Count, items = preview });
    }
    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
    {
        return Results.Problem(
            title: "Metadata preview failed",
            detail: ex.Message,
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapPost("/api/v1/shares", async (ShareRequest request, IShareRepository repository, CancellationToken ct) =>
{
    var validation = Validate(request, allowedRoots);
    if (validation is not null) return validation;

    var share = ToShare(Guid.NewGuid(), request);
    try
    {
        await repository.UpsertAsync(share, ct);
        return Results.Created($"/api/v1/shares/{share.Id}", share);
    }
    catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
    {
        return Results.Conflict(new { error = "A share with this path already exists." });
    }
});

app.MapPut("/api/v1/shares/{id:guid}", async (Guid id, ShareRequest request, IShareRepository repository, CancellationToken ct) =>
{
    if (await repository.GetAsync(id, ct) is null) return Results.NotFound();
    var validation = Validate(request, allowedRoots);
    if (validation is not null) return validation;

    var share = ToShare(id, request);
    try
    {
        await repository.UpsertAsync(share, ct);
        return Results.Ok(share);
    }
    catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
    {
        return Results.Conflict(new { error = "A share with this path already exists." });
    }
});

app.MapDelete("/api/v1/shares/{id:guid}", async (Guid id, IShareRepository repository, CancellationToken ct) =>
{
    try
    {
        return await repository.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound();
    }
    catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
    {
        return Results.Conflict(new { error = "Share is still referenced by a source group, event or indexed media file." });
    }
});

app.MapSourceGroupEndpoints();
app.MapEventEndpoints();
app.MapRoutingEndpoints();
app.MapRenameEndpoints();
app.MapTransferEndpoints();
app.MapSettingsEndpoints();
app.MapMaintenanceEndpoints();
app.MapUpdateEndpoints();

app.Run();

static IResult? Validate(ShareRequest request, IReadOnlyCollection<string> allowedRoots)
{
    if (string.IsNullOrWhiteSpace(request.Name))
        return Results.BadRequest(new { error = "Name is required." });

    if (string.IsNullOrWhiteSpace(request.Path) || !Path.IsPathRooted(request.Path))
        return Results.BadRequest(new { error = "Path must be an absolute path visible inside the container." });

    if (!IsPathAllowed(request.Path, allowedRoots))
        return Results.BadRequest(new { error = "Path is outside the configured MomentFerry:AllowedRoots.", allowedRoots });

    if (request.StabilitySeconds is < 1 or > 3600)
        return Results.BadRequest(new { error = "StabilitySeconds must be between 1 and 3600." });

    if (request.AllowedMediaTypes is null || request.AllowedMediaTypes.Length == 0)
        return Results.BadRequest(new { error = "At least one media type must be enabled." });

    if (InvalidSubfolder(request.ImageSubfolder))
        return Results.BadRequest(new { error = "ImageSubfolder must be a relative folder without '..' segments." });

    if (InvalidSubfolder(request.VideoSubfolder))
        return Results.BadRequest(new { error = "VideoSubfolder must be a relative folder without '..' segments." });

    return null;
}

static bool InvalidSubfolder(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return false;
    if (Path.IsPathRooted(value)) return true;

    return value
        .Replace('\\', '/')
        .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Any(segment => segment is ".." or ".");
}

static bool IsPathAllowed(string candidate, IEnumerable<string> roots)
{
    var fullCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    foreach (var root in roots)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(fullCandidate, fullRoot, comparison)) return true;
        if (fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison)) return true;
    }

    return false;
}

static Share ToShare(Guid id, ShareRequest request) => new()
{
    Id = id,
    Name = request.Name.Trim(),
    Path = Path.GetFullPath(request.Path.Trim()),
    Role = request.Role,
    Enabled = request.Enabled,
    Owner = string.IsNullOrWhiteSpace(request.Owner) ? null : request.Owner.Trim(),
    Group = string.IsNullOrWhiteSpace(request.Group) ? null : request.Group.Trim(),
    Preset = string.IsNullOrWhiteSpace(request.Preset) ? null : request.Preset.Trim(),
    StabilitySeconds = request.StabilitySeconds,
    Recursive = request.Recursive,
    DefaultTimeZone = string.IsNullOrWhiteSpace(request.DefaultTimeZone) ? null : request.DefaultTimeZone.Trim(),
    IgnorePatterns = request.IgnorePatterns?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct().ToArray() ?? Array.Empty<string>(),
    AllowedMediaTypes = request.AllowedMediaTypes!.ToHashSet(),
    ImageExtensions = MediaExtensionDefaults.Normalize(request.ImageExtensions),
    VideoExtensions = MediaExtensionDefaults.Normalize(request.VideoExtensions),
    ImageSubfolder = string.IsNullOrWhiteSpace(request.ImageSubfolder) ? null : request.ImageSubfolder.Trim(),
    VideoSubfolder = string.IsNullOrWhiteSpace(request.VideoSubfolder) ? null : request.VideoSubfolder.Trim(),
    RenamePresetId = request.RenamePresetId
};

public sealed record ShareRequest(
    string Name,
    string Path,
    ShareRole Role = ShareRole.Source,
    bool Enabled = true,
    string? Owner = null,
    string? Group = null,
    string? Preset = null,
    int StabilitySeconds = 30,
    bool Recursive = true,
    string? DefaultTimeZone = null,
    string[]? IgnorePatterns = null,
    MediaType[]? AllowedMediaTypes = null,
    string[]? ImageExtensions = null,
    string[]? VideoExtensions = null,
    string? ImageSubfolder = null,
    string? VideoSubfolder = null,
    Guid? RenamePresetId = null);
