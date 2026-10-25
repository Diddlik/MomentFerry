using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MomentFerry.Web.Diagnostics;

namespace MomentFerry.Tests;

public sealed class RequestLoggingMiddlewareTests
{
    [Fact]
    public async Task ChangingRequest_IsLoggedAtInformation()
    {
        var logger = new CapturingLogger();
        var context = Context("/api/v1/transfers", "POST");

        await Middleware(_ => Task.CompletedTask, logger).InvokeAsync(context);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("POST /api/v1/transfers returned 200", entry.Message);
    }

    [Fact]
    public async Task FastRead_StaysAtDebug()
    {
        var logger = new CapturingLogger();

        await Middleware(_ => Task.CompletedTask, logger).InvokeAsync(Context("/api/v1/status"));

        Assert.Equal(LogLevel.Debug, Assert.Single(logger.Entries).Level);
    }

    [Fact]
    public async Task TemplatePreview_IsTreatedAsARead()
    {
        var logger = new CapturingLogger();

        await Middleware(_ => Task.CompletedTask, logger).InvokeAsync(Context("/api/v1/rename-presets/preview", "POST"));

        Assert.Equal(LogLevel.Debug, Assert.Single(logger.Entries).Level);
    }

    [Fact]
    public async Task RefusedRequest_IsAWarning()
    {
        var logger = new CapturingLogger();

        await Middleware(context =>
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Task.CompletedTask;
        }, logger).InvokeAsync(Context("/api/v1/transfers", "POST"));

        Assert.Equal(LogLevel.Warning, Assert.Single(logger.Entries).Level);
    }

    [Fact]
    public async Task RefusedRequest_CarriesTheReasonTheEndpointGave()
    {
        var logger = new CapturingLogger();
        var context = Context("/api/v1/shares", "POST");
        var client = new MemoryStream();
        context.Response.Body = client;

        await Middleware(async http =>
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsJsonAsync(new { error = "StabilitySeconds must be between 1 and 3600." });
        }, logger).InvokeAsync(context);

        Assert.EndsWith(": StabilitySeconds must be between 1 and 3600.", Assert.Single(logger.Entries).Message);
        Assert.Same(client, context.Response.Body);
        Assert.Contains("StabilitySeconds", System.Text.Encoding.UTF8.GetString(client.ToArray()));
    }

    [Fact]
    public async Task AbandonedRequest_IsReportedAsAClientDisconnect()
    {
        var logger = new CapturingLogger();
        var aborted = new CancellationTokenSource();
        var context = Context("/api/v1/shares/1/scan");
        context.RequestAborted = aborted.Token;

        await Assert.ThrowsAsync<OperationCanceledException>(() => Middleware(_ =>
        {
            aborted.Cancel();
            throw new OperationCanceledException(aborted.Token);
        }, logger).InvokeAsync(context));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("client disconnected", entry.Message);
    }

    [Fact]
    public async Task UnhandledError_IsLoggedWithTheExceptionAndRethrown()
    {
        var logger = new CapturingLogger();
        var failure = new InvalidOperationException("boom");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Middleware(_ => throw failure, logger).InvokeAsync(Context("/api/v1/transfers", "POST")));

        Assert.Same(failure, thrown);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(failure, entry.Exception);
    }

    [Fact]
    public async Task StaticFiles_AreNotLogged()
    {
        var logger = new CapturingLogger();

        await Middleware(_ => Task.CompletedTask, logger).InvokeAsync(Context("/app.js"));

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void ActivityLog_KeepsTheFullExceptionAsDetail()
    {
        var log = new ActivityLog();
        var logger = new ActivityLogProvider(log).CreateLogger("MomentFerry.Tests");
        Exception failure;
        try { throw new IOException("disk gone"); }
        catch (IOException ex) { failure = ex; }

        logger.LogError(failure, "Copy failed");

        var entry = Assert.Single(log.Recent(10, LogLevel.Information));
        Assert.Equal("Copy failed — disk gone", entry.Message);
        Assert.Contains("System.IO.IOException: disk gone", entry.Detail);
        Assert.Contains(nameof(ActivityLog_KeepsTheFullExceptionAsDetail), entry.Detail);
    }

    private static RequestLoggingMiddleware Middleware(RequestDelegate next, CapturingLogger logger) =>
        new(next, logger);

    private static DefaultHttpContext Context(string path, string method = "GET")
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = method;
        return context;
    }

    private sealed class CapturingLogger : ILogger<RequestLoggingMiddleware>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
