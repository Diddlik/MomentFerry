using System.Text.Json;
using MomentFerry.Application.Abstractions;

namespace MomentFerry.Infrastructure.Runtime;

public sealed class JsonRuntimeSettingsStore(
    string path,
    MomentFerryRuntimeSettings defaults) : IRuntimeSettingsStore
{
    private readonly string _path = Path.GetFullPath(path);
    private readonly MomentFerryRuntimeSettings _defaults = Normalize(defaults);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MomentFerryRuntimeSettings? _cached;

    public async Task<MomentFerryRuntimeSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cached is not null) return _cached;
            if (!File.Exists(_path))
            {
                _cached = _defaults;
                return _cached;
            }

            await using var stream = File.OpenRead(_path);
            var loaded = await JsonSerializer.DeserializeAsync<MomentFerryRuntimeSettings>(stream, cancellationToken: cancellationToken);
            _cached = Normalize(loaded ?? _defaults);
            return _cached;
        }
        catch (JsonException)
        {
            _cached = _defaults;
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MomentFerryRuntimeSettings> UpdateAsync(
        MomentFerryRuntimeSettings settings,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(settings);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            var tempPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    16 * 1024,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(
                        stream,
                        normalized,
                        new JsonSerializerOptions { WriteIndented = true },
                        cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }

                File.Move(tempPath, _path, overwrite: true);
                _cached = normalized;
                return normalized;
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MomentFerryRuntimeSettings> ResetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
            _cached = _defaults;
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static MomentFerryRuntimeSettings Normalize(MomentFerryRuntimeSettings settings) => settings with
    {
        ReconciliationIntervalSeconds = Math.Clamp(settings.ReconciliationIntervalSeconds, 15, 86400),
        MaxFilesPerSharePerCycle = Math.Clamp(settings.MaxFilesPerSharePerCycle, 1, 2000),
        MaxParallelMetadataReads = Math.Clamp(settings.MaxParallelMetadataReads, 1, 8),
        MinimumFreeSpaceReserveBytes = Math.Clamp(settings.MinimumFreeSpaceReserveBytes, 0, 1L * 1024 * 1024 * 1024 * 1024),
        OperationRetentionDays = Math.Clamp(settings.OperationRetentionDays, 0, 3650)
    };
}
