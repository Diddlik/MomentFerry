namespace MomentFerry.Application.Abstractions;

public sealed record MomentFerryRuntimeSettings(
    bool DryRun = true,
    bool AutomationEnabled = true,
    int ReconciliationIntervalSeconds = 1800,
    int MaxFilesPerSharePerCycle = 200,
    int MaxParallelMetadataReads = 2,
    bool AllowFilesystemTimestampFallback = false,
    long MinimumFreeSpaceReserveBytes = 512L * 1024L * 1024L,
    bool AutomaticImageUpdatesEnabled = false,
    // 0 keeps the operation history for good. Anything still waiting for a decision is never removed,
    // however old it is, so a retention window cannot bury an unresolved file.
    int OperationRetentionDays = 0,
    bool PasswordProtectionEnabled = false);

public interface IRuntimeSettingsStore
{
    Task<MomentFerryRuntimeSettings> GetAsync(CancellationToken cancellationToken = default);
    Task<MomentFerryRuntimeSettings> UpdateAsync(
        MomentFerryRuntimeSettings settings,
        CancellationToken cancellationToken = default);
    Task<MomentFerryRuntimeSettings> ResetAsync(CancellationToken cancellationToken = default);
}
