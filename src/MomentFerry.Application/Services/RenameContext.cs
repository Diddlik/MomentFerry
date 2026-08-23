using MomentFerry.Application.Abstractions;
using MomentFerry.Core.Domain;

namespace MomentFerry.Application.Services;

/// <summary>
/// The rename configuration a routing decision needs, loaded once per cycle so resolving a path stays
/// a pure computation instead of a database call per file.
/// </summary>
public sealed record RenameContext(
    IReadOnlyDictionary<Guid, RenamePreset> Presets,
    IReadOnlyDictionary<string, string> CameraNames)
{
    public static readonly RenameContext Empty = new(
        new Dictionary<Guid, RenamePreset>(),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    public RenamePreset? PresetFor(Share? share)
        => share?.RenamePresetId is { } id && Presets.TryGetValue(id, out var preset) ? preset : null;
}

public sealed class RenameContextFactory(
    IRenamePresetRepository presets,
    ICameraMappingRepository cameraMappings)
{
    public async Task<RenameContext> LoadAsync(CancellationToken cancellationToken = default)
    {
        var allPresets = await presets.ListAsync(cancellationToken);
        var allMappings = await cameraMappings.ListAsync(cancellationToken);
        return new RenameContext(
            allPresets.ToDictionary(x => x.Id),
            FileNameTemplate.BuildCameraNames(allMappings));
    }
}
