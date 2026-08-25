using MomentFerry.Application.Abstractions;

namespace MomentFerry.Web.Updates;

public sealed class ImageUpdateWorker(
    ImageUpdateService updates,
    IRuntimeSettingsStore settings,
    IConfiguration configuration,
    ILogger<ImageUpdateWorker> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The first check runs shortly after start, not one full period later: a container restarted
        // more often than the period never checked at all, and enabling the toggle appeared to do
        // nothing for six hours because the running timer kept its own schedule.
        var initialDelay = TimeSpan.FromSeconds(
            Math.Clamp(configuration.GetValue("MomentFerry:Updates:InitialDelaySeconds", 30), 0, 300));
        if (initialDelay > TimeSpan.Zero)
            await Task.Delay(initialDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await CheckAndInstallAsync(stoppingToken);
            try
            {
                await Task.Delay(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// One automatic pass. Every outcome is logged, because an update that silently never arrives is
    /// indistinguishable from a broken toggle: the Activity log is the only place a user can see
    /// whether the check ran, what it found, and why nothing was installed.
    /// </summary>
    public async Task CheckAndInstallAsync(CancellationToken cancellationToken)
    {
        if (!(await settings.GetAsync(cancellationToken)).AutomaticImageUpdatesEnabled) return;

        try
        {
            var status = await updates.CheckAsync(cancellationToken);
            if (status.LastError is not null)
            {
                logger.LogWarning("Automatic image update check failed: {Error}", status.LastError);
                return;
            }

            if (!status.UpdateAvailable)
            {
                logger.LogInformation(
                    "Automatic image update check: running {Running}, latest {Latest}, nothing to install",
                    status.RunningVersion,
                    status.LatestVersion ?? "unknown");
                return;
            }

            if (!status.UpdaterConfigured)
            {
                logger.LogWarning(
                    "Automatic image update skipped: {Latest} is available but no updater companion is configured",
                    status.LatestVersion);
                return;
            }

            logger.LogInformation(
                "Automatic image update: requesting {Latest} over {Running}",
                status.LatestVersion,
                status.RunningVersion);
            await updates.InstallAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            logger.LogError(ex, "Automatic image update failed");
        }
    }
}
