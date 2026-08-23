using System.Net.Http.Headers;
using System.Net.Http.Json;
using MomentFerry.Application.Abstractions;

namespace MomentFerry.Web.Updates;

public sealed record ImageUpdateOptions(
    string ReleaseApiUrl,
    string? WatchtowerUrl,
    string? WatchtowerToken,
    string RunningVersion);

public sealed record ImageUpdateStatus(
    string RunningVersion,
    string? LatestVersion,
    bool UpdateAvailable,
    string? Changelog,
    DateTimeOffset? PublishedAt,
    string? ReleaseUrl,
    bool AutomaticUpdatesEnabled,
    bool UpdaterConfigured,
    DateTimeOffset? LastCheckedAt,
    DateTimeOffset? LastUpdateRequestedAt,
    DateTimeOffset? LastUpdateCompletedAt,
    string? LastError,
    string? RunningVersionUrl = null);

public sealed class ImageUpdateService(
    HttpClient http,
    ImageUpdateOptions options,
    IRuntimeSettingsStore settings,
    IImageUpdateStatusStore statusStore,
    IClock clock)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SemaphoreSlim installGate = new(1, 1);
    private ImageUpdateStatus? status;

    public async Task<ImageUpdateStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var runtime = await settings.GetAsync(cancellationToken);
        status ??= await statusStore.LoadAsync(cancellationToken);
        var current = (status ?? EmptyStatus(runtime.AutomaticImageUpdatesEnabled)) with
        {
            RunningVersion = options.RunningVersion,
            AutomaticUpdatesEnabled = runtime.AutomaticImageUpdatesEnabled,
            RunningVersionUrl = RunningVersionUrl
        };
        if (current.LastUpdateRequestedAt is not null &&
            (current.LastUpdateCompletedAt is null || current.LastUpdateRequestedAt > current.LastUpdateCompletedAt) &&
            VersionsEqual(current.LatestVersion, options.RunningVersion))
        {
            current = current with
            {
                UpdateAvailable = false,
                LastUpdateCompletedAt = clock.UtcNow,
                LastError = null
            };
            status = await PersistAsync(current, cancellationToken);
        }
        return current;
    }

    public async Task<ImageUpdateStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var runtime = await settings.GetAsync(cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Get, options.ReleaseApiUrl);
            request.Headers.UserAgent.ParseAdd("MomentFerry/1.0");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                status = EmptyStatus(runtime.AutomaticImageUpdatesEnabled) with
                {
                    LastCheckedAt = clock.UtcNow,
                    LastError = "No stable release has been published yet."
                };
                return await PersistAsync(status, cancellationToken);
            }
            response.EnsureSuccessStatusCode();
            var release = await response.Content.ReadFromJsonAsync<GitHubRelease>(cancellationToken) ??
                throw new InvalidOperationException("Release service returned an empty response.");
            var latest = release.TagName.TrimStart('v', 'V');
            status = new ImageUpdateStatus(
                options.RunningVersion,
                latest,
                IsNewer(latest, options.RunningVersion),
                release.Body,
                release.PublishedAt,
                release.HtmlUrl,
                runtime.AutomaticImageUpdatesEnabled,
                IsUpdaterConfigured(),
                clock.UtcNow,
                status?.LastUpdateRequestedAt,
                status?.LastUpdateCompletedAt,
                null);
            return await PersistAsync(status, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            var runtime = await settings.GetAsync(CancellationToken.None);
            status = (status ?? EmptyStatus(runtime.AutomaticImageUpdatesEnabled)) with
            {
                LastCheckedAt = clock.UtcNow,
                LastError = ex.Message
            };
            return await PersistAsync(status, CancellationToken.None);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ImageUpdateStatus> InstallAsync(CancellationToken cancellationToken = default)
    {
        await installGate.WaitAsync(cancellationToken);
        ImageUpdateStatus? current = null;
        try
        {
            if (!IsUpdaterConfigured()) throw new InvalidOperationException("The updater companion is not configured.");
            current = await CheckAsync(cancellationToken);
            if (!current.UpdateAvailable) throw new InvalidOperationException("No newer stable image is available.");

            status = current with { LastUpdateRequestedAt = clock.UtcNow, LastError = null };
            await PersistAsync(status, cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(options.WatchtowerUrl!), "v1/update"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.WatchtowerToken);
            using var response = await http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            return status;
        }
        catch (HttpRequestException ex)
        {
            var failed = (status ?? current ?? await GetStatusAsync(CancellationToken.None)) with
            {
                LastError = $"Updater request failed: {ex.Message}"
            };
            status = await PersistAsync(failed, CancellationToken.None);
            throw new InvalidOperationException(status.LastError, ex);
        }
        finally
        {
            installGate.Release();
        }
    }

    /// <summary>
    /// Builds the GitHub release page for a version from the configured release API URL, so the repository
    /// location stays configurable instead of being hardcoded in the browser UI. Returns null for the
    /// placeholder version and for a release API URL that is not a GitHub repository endpoint.
    /// </summary>
    public static string? BuildReleaseTagUrl(string releaseApiUrl, string version)
    {
        if (string.IsNullOrWhiteSpace(version) || version.StartsWith("0.0.0", StringComparison.Ordinal)) return null;
        if (!Uri.TryCreate(releaseApiUrl, UriKind.Absolute, out var api)) return null;
        if (!api.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)) return null;

        var segments = api.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3 || !segments[0].Equals("repos", StringComparison.OrdinalIgnoreCase)) return null;

        var tag = version.StartsWith('v') ? version : "v" + version;
        return $"https://github.com/{segments[1]}/{segments[2]}/releases/tag/{Uri.EscapeDataString(tag)}";
    }

    private string? RunningVersionUrl => BuildReleaseTagUrl(options.ReleaseApiUrl, options.RunningVersion);

    private bool IsUpdaterConfigured() =>
        Uri.TryCreate(options.WatchtowerUrl, UriKind.Absolute, out _) && !string.IsNullOrWhiteSpace(options.WatchtowerToken);

    private ImageUpdateStatus EmptyStatus(bool automatic) => new(
        options.RunningVersion, null, false, null, null, null, automatic, IsUpdaterConfigured(), null, null, null, null);

    private static bool IsNewer(string latest, string running)
    {
        var latestParts = latest.Split('+')[0].Split('-', 2);
        var runningParts = running.Split('+')[0].Split('-', 2);
        if (!Version.TryParse(latestParts[0], out var latestVersion) ||
            !Version.TryParse(runningParts[0], out var runningVersion)) return false;
        var comparison = latestVersion.CompareTo(runningVersion);
        return comparison > 0 || comparison == 0 && latestParts.Length == 1 && runningParts.Length == 2;
    }

    private static bool VersionsEqual(string? left, string right) =>
        !string.IsNullOrWhiteSpace(left) &&
        string.Equals(left.TrimStart('v', 'V'), right.TrimStart('v', 'V'), StringComparison.OrdinalIgnoreCase);

    private async Task<ImageUpdateStatus> PersistAsync(ImageUpdateStatus value, CancellationToken cancellationToken)
    {
        await statusStore.SaveAsync(value, cancellationToken);
        return value with { RunningVersionUrl = RunningVersionUrl };
    }

    private sealed record GitHubRelease(
        [property: System.Text.Json.Serialization.JsonPropertyName("tag_name")] string TagName,
        [property: System.Text.Json.Serialization.JsonPropertyName("body")] string? Body,
        [property: System.Text.Json.Serialization.JsonPropertyName("published_at")] DateTimeOffset? PublishedAt,
        [property: System.Text.Json.Serialization.JsonPropertyName("html_url")] string? HtmlUrl);
}
