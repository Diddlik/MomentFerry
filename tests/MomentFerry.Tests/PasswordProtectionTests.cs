using Microsoft.AspNetCore.Http;
using MomentFerry.Application.Abstractions;
using MomentFerry.Web.Security;

namespace MomentFerry.Tests;

public sealed class PasswordProtectionTests
{
    [Fact]
    public void CredentialsRequireExactUsernameAndPassword()
    {
        var options = new PasswordProtectionOptions("owner", "correct horse battery staple");

        Assert.True(options.IsConfigured);
        Assert.True(options.Matches("owner", "correct horse battery staple"));
        Assert.False(options.Matches("Owner", "correct horse battery staple"));
        Assert.False(options.Matches("owner", "wrong"));
        Assert.False(options.Matches(null, null));
        Assert.False(new PasswordProtectionOptions("owner", "too-short").IsConfigured);
    }

    [Fact]
    public void RevokedSessionCannotBeReused()
    {
        var options = new PasswordProtectionOptions("owner", "long-enough-secret");
        var principal = options.CreatePrincipal();

        Assert.True(options.HasValidSession(principal));
        options.RevokeSession(principal);
        Assert.False(options.HasValidSession(principal));
    }

    [Fact]
    public async Task DisabledProtectionAllowsRequests()
    {
        var called = false;
        var middleware = CreateMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(
            Context("/api/v1/settings"),
            new StubSettingsStore(new(PasswordProtectionEnabled: false)));

        Assert.True(called);
    }

    [Fact]
    public async Task EnabledProtectionRedirectsPageAndRejectsApi()
    {
        var settings = new StubSettingsStore(new(PasswordProtectionEnabled: true));
        var page = Context("/settings");
        var api = Context("/api/v1/settings");
        var middleware = CreateMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(page, settings);
        await middleware.InvokeAsync(api, settings);

        Assert.Equal(StatusCodes.Status302Found, page.Response.StatusCode);
        Assert.StartsWith("/login.html?returnUrl=", page.Response.Headers.Location.ToString());
        Assert.Equal(StatusCodes.Status401Unauthorized, api.Response.StatusCode);
    }

    [Fact]
    public async Task HealthRemainsPublicWhenProtectionIsEnabled()
    {
        var called = false;
        var middleware = CreateMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(
            Context("/health"),
            new StubSettingsStore(new(PasswordProtectionEnabled: true)));

        Assert.True(called);
    }

    [Fact]
    public async Task AuthenticatedMutationRequiresSameOriginHeader()
    {
        var options = new PasswordProtectionOptions("owner", "secret");
        var context = Context("/api/v1/settings", HttpMethods.Put);
        context.User = options.CreatePrincipal();
        var middleware = CreateMiddleware(_ => Task.CompletedTask, options);

        await middleware.InvokeAsync(
            context,
            new StubSettingsStore(new(PasswordProtectionEnabled: true)));

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task AuthenticatedMutationWithSameOriginHeaderIsAllowed()
    {
        var called = false;
        var options = new PasswordProtectionOptions("owner", "secret");
        var context = Context("/api/v1/settings", HttpMethods.Put);
        context.User = options.CreatePrincipal();
        context.Request.Headers[PasswordProtectionOptions.CsrfHeader] = "1";
        var middleware = CreateMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        }, options);

        await middleware.InvokeAsync(
            context,
            new StubSettingsStore(new(PasswordProtectionEnabled: true)));

        Assert.True(called);
    }

    private static PasswordProtectionMiddleware CreateMiddleware(
        RequestDelegate next,
        PasswordProtectionOptions? options = null) =>
        new(next, options ?? new PasswordProtectionOptions("owner", "secret"));

    private static DefaultHttpContext Context(string path, string method = "GET")
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = method;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class StubSettingsStore(MomentFerryRuntimeSettings settings) : IRuntimeSettingsStore
    {
        public Task<MomentFerryRuntimeSettings> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(settings);

        public Task<MomentFerryRuntimeSettings> UpdateAsync(
            MomentFerryRuntimeSettings value,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(value);

        public Task<MomentFerryRuntimeSettings> ResetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(settings);
    }
}
