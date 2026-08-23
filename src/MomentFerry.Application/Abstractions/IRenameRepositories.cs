using MomentFerry.Core.Domain;

namespace MomentFerry.Application.Abstractions;

public interface IRenamePresetRepository
{
    Task<IReadOnlyList<RenamePreset>> ListAsync(CancellationToken cancellationToken = default);
    Task<RenamePreset?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task UpsertAsync(RenamePreset preset, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface ICameraMappingRepository
{
    Task<IReadOnlyList<CameraMapping>> ListAsync(CancellationToken cancellationToken = default);
    Task UpsertAsync(CameraMapping mapping, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
