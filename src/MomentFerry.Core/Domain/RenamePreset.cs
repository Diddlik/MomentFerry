namespace MomentFerry.Core.Domain;

/// <summary>
/// A named filename template. A source preset normalizes incoming names, and the destination preset
/// then shapes the result, so the two chain rather than compete.
/// </summary>
public sealed class RenamePreset
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; init; }
    public required string Template { get; init; }
}
