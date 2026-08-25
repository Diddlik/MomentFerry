using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MomentFerry.Application.Abstractions;
using MomentFerry.Web.Updates;
using MomentFerry.Web.Api;

namespace MomentFerry.Tests;

public sealed class ImageUpdateServiceTests
{
    [Fact]
    public async Task Check_ExposesNewVersionAndChangelog()
    {
        var handler = new QueueHandler(JsonResponse("""
            {"tag_name":"v1.2.0","body":"Important fixes","published_at":"2026-08-21T18:00:00Z","html_url":"https://example.test/release"}
            """));
        var service = CreateService(handler);

        var status = await service.CheckAsync();

        Assert.True(status.UpdateAvailable);
        Assert.Equal("1.2.0", status.LatestVersion);
        Assert.Equal("Important fixes", status.Changelog);
    }

    [Fact]
    public async Task Check_StableReleaseIsNewerThanMatchingPrerelease()
    {
        var handler = new QueueHandler(JsonResponse("""{"tag_name":"v1.0.0","body":"Stable"}"""));
        var service = CreateService(handler, runningVersion: "1.0.0-beta.2");

        var status = await service.CheckAsync();

        Assert.True(status.UpdateAvailable);
    }

    [Fact]
    public async Task Install_UsesAuthenticatedUpdaterCompanion()
    {
        var handler = new QueueHandler(
            JsonResponse("""{"tag_name":"v1.2.0","body":"Fixes","published_at":"2026-08-21T18:00:00Z"}"""),
            new HttpResponseMessage(HttpStatusCode.OK));
        var service = CreateService(handler);

        var status = await service.InstallAsync();

        Assert.NotNull(status.LastUpdateRequestedAt);
        Assert.Equal("http://updater:8080/v1/update", handler.Requests[1].Uri);
        Assert.Equal("Bearer secret-token", handler.Requests[1].Authorization);
    }

    [Fact]
    public async Task Check_NetworkFailureIsExposedAsStatus()
    {
        var service = CreateService(new ThrowingHandler());

        var status = await service.CheckAsync();

        Assert.False(status.UpdateAvailable);
        Assert.Contains("release service unavailable", status.LastError);
    }

    [Fact]
    public async Task Install_UpdaterFailureIsPersistedAndReported()
    {
        var statusStore = new MemoryStatusStore();
        var handler = new QueueHandler(
            JsonResponse("""{"tag_name":"v1.2.0","body":"Fixes"}"""),
            new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var service = CreateService(handler, statusStore);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.InstallAsync());
        var status = await statusStore.LoadAsync();

        Assert.Contains("Updater request failed", error.Message);
        Assert.Contains("500", status!.LastError);
        Assert.NotNull(status.LastUpdateRequestedAt);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("install_update", false)]
    [InlineData("INSTALL_UPDATE", true)]
    public void InstallConfirmation_IsExact(string? confirmation, bool expected) =>
        Assert.Equal(expected, new ImageUpdateRequest(confirmation).IsConfirmed);

    [Fact]
    public async Task JsonStatusStore_PersistsLatestCheckAcrossInstances()
    {
        var directory = Path.Combine(Path.GetTempPath(), "momentferry-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "update-status.json");
        try
        {
            var expected = await CreateService(new QueueHandler(JsonResponse(
                """{"tag_name":"v1.2.0","body":"Persist me","published_at":"2026-08-21T18:00:00Z"}""")),
                new JsonImageUpdateStatusStore(path)).CheckAsync();

            var actual = await new JsonImageUpdateStatusStore(path).LoadAsync();

            Assert.Equal(expected, actual);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task GetStatus_MarksRequestedVersionCompletedAfterHealthyRestart()
    {
        var statusStore = new MemoryStatusStore();
        await statusStore.SaveAsync(new ImageUpdateStatus(
            "1.0.0", "1.2.0", true, "Fixes", null, null, false, true,
            DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow.AddMinutes(-1), null, null));
        var service = CreateService(new QueueHandler(), statusStore, runningVersion: "1.2.0");

        var status = await service.GetStatusAsync();

        Assert.Equal("1.2.0", status.RunningVersion);
        Assert.False(status.UpdateAvailable);
        Assert.Equal(new DateTimeOffset(2026, 8, 21, 20, 0, 0, TimeSpan.Zero), status.LastUpdateCompletedAt);
        Assert.Null(status.LastError);
    }

    [Fact]
    public async Task AutomaticUpdates_CheckAndInstallWithoutWaitingForTheFirstPeriod()
    {
        // The worker used to wait one full six-hour period before its first check, so a container that
        // restarts more often never updated itself and enabling the toggle looked broken.
        var handler = new QueueHandler(
            JsonResponse("""{"tag_name":"v1.2.0","body":"Fixes","published_at":"2026-08-21T18:00:00Z"}"""),
            JsonResponse("""{"tag_name":"v1.2.0","body":"Fixes","published_at":"2026-08-21T18:00:00Z"}"""),
            new HttpResponseMessage(HttpStatusCode.OK));
        var settings = new MemorySettingsStore();
        await settings.UpdateAsync(new MomentFerryRuntimeSettings { AutomaticImageUpdatesEnabled = true });
        var worker = CreateWorker(CreateService(handler), settings);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitForRequestsAsync(handler, 3);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.Equal(
            "http://updater:8080/v1/update",
            handler.Requests[^1].Uri);
        Assert.Equal("Bearer secret-token", handler.Requests[^1].Authorization);
    }

    [Fact]
    public async Task AutomaticUpdates_DisabledToggleNeverContactsTheReleaseService()
    {
        var handler = new QueueHandler();
        var worker = CreateWorker(CreateService(handler), new MemorySettingsStore());

        await worker.CheckAndInstallAsync(CancellationToken.None);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AutomaticUpdates_EnablingTheToggleWhileRunningChecksWithoutWaitingOutThePeriod()
    {
        // The startup check alone does not cover this: the toggle is usually switched on while the
        // container is already running, and a six-hour wait then looks exactly like a dead toggle.
        var handler = new QueueHandler(
            JsonResponse("""{"tag_name":"v1.2.0","body":"Fixes","published_at":"2026-08-21T18:00:00Z"}"""),
            JsonResponse("""{"tag_name":"v1.2.0","body":"Fixes","published_at":"2026-08-21T18:00:00Z"}"""),
            new HttpResponseMessage(HttpStatusCode.OK));
        var settings = new MemorySettingsStore();
        var wakeSignal = new ImageUpdateWakeSignal();
        var worker = CreateWorker(CreateService(handler), settings, wakeSignal);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            // The first pass finds the toggle off and contacts nothing.
            await Task.Delay(200);
            Assert.Empty(handler.Requests);

            await settings.UpdateAsync(new MomentFerryRuntimeSettings { AutomaticImageUpdatesEnabled = true });
            wakeSignal.Wake();
            await WaitForRequestsAsync(handler, 3);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.Equal("http://updater:8080/v1/update", handler.Requests[^1].Uri);
    }

    private static ImageUpdateWorker CreateWorker(
        ImageUpdateService service,
        IRuntimeSettingsStore settings,
        ImageUpdateWakeSignal? wakeSignal = null) => new(
        service,
        settings,
        wakeSignal ?? new ImageUpdateWakeSignal(),
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MomentFerry:Updates:InitialDelaySeconds"] = "0" })
            .Build(),
        NullLogger<ImageUpdateWorker>.Instance);

    private static async Task WaitForRequestsAsync(QueueHandler handler, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (handler.Requests.Count < count && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.Equal(count, handler.Requests.Count);
    }

    private static ImageUpdateService CreateService(
        HttpMessageHandler handler,
        IImageUpdateStatusStore? statusStore = null,
        string runningVersion = "1.0.0") => new(
        new HttpClient(handler),
        new ImageUpdateOptions(
            "https://api.example.test/releases/latest",
            "http://updater:8080/",
            "secret-token",
            runningVersion),
        new MemorySettingsStore(),
        statusStore ?? new MemoryStatusStore(),
        new FixedClock());

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private sealed class QueueHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses = new(responses);
        public List<(string Uri, string? Authorization)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.ToString(), request.Headers.Authorization?.ToString()));
            return Task.FromResult(responses.Dequeue());
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("release service unavailable");
    }

    [Theory]
    [InlineData("1.3.0", "https://github.com/diddlik/MomentFerry/releases/tag/v1.3.0")]
    [InlineData("v1.3.0", "https://github.com/diddlik/MomentFerry/releases/tag/v1.3.0")]
    [InlineData("1.4.0-rc.1", "https://github.com/diddlik/MomentFerry/releases/tag/v1.4.0-rc.1")]
    public void BuildReleaseTagUrl_DerivesTheReleasePageFromTheConfiguredApiUrl(string version, string expected)
    {
        var url = ImageUpdateService.BuildReleaseTagUrl(
            "https://api.github.com/repos/diddlik/MomentFerry/releases/latest",
            version);

        Assert.Equal(expected, url);
    }

    [Theory]
    [InlineData("0.0.0")]
    [InlineData("0.0.0-dev.42")]
    [InlineData("")]
    public void BuildReleaseTagUrl_SkipsPlaceholderVersionsThatHaveNoTag(string version)
    {
        Assert.Null(ImageUpdateService.BuildReleaseTagUrl(
            "https://api.github.com/repos/diddlik/MomentFerry/releases/latest",
            version));
    }

    [Theory]
    [InlineData("https://gitea.example.com/api/v1/repos/x/y/releases/latest")]
    [InlineData("not-a-url")]
    [InlineData("https://api.github.com/rate_limit")]
    public void BuildReleaseTagUrl_ReturnsNullForNonGitHubRepositoryEndpoints(string apiUrl)
    {
        Assert.Null(ImageUpdateService.BuildReleaseTagUrl(apiUrl, "1.3.0"));
    }

    private sealed class MemorySettingsStore : IRuntimeSettingsStore
    {
        private MomentFerryRuntimeSettings settings = new();
        public Task<MomentFerryRuntimeSettings> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(settings);
        public Task<MomentFerryRuntimeSettings> UpdateAsync(MomentFerryRuntimeSettings value, CancellationToken cancellationToken = default) =>
            Task.FromResult(settings = value);
        public Task<MomentFerryRuntimeSettings> ResetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(settings = new());
    }

    private sealed class MemoryStatusStore : IImageUpdateStatusStore
    {
        private ImageUpdateStatus? status;
        public Task<ImageUpdateStatus?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(status);
        public Task SaveAsync(ImageUpdateStatus value, CancellationToken cancellationToken = default)
        {
            status = value;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 8, 21, 20, 0, 0, TimeSpan.Zero);
    }
}
