using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MomentFerry.Application.Abstractions;

namespace MomentFerry.Web.Security;

public sealed class PasswordProtectionOptions(string? username, string? password)
{
    public const string AuthenticationScheme = "MomentFerry";
    public const string CsrfHeader = "X-MomentFerry-Request";
    public const string LoginRateLimitPolicy = "login";
    private const string SessionIdClaim = "momentferry:session";
    private readonly string _username = username ?? string.Empty;
    private readonly string _password = password ?? string.Empty;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _sessions = new();

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_username) && _username.Length <= 256 &&
        _password.Length is >= 12 and <= 1024;

    /// <summary>
    /// A human-readable reason the credentials are not usable, or null when they are.
    /// Returns null when nothing is configured at all, so the UI can show the initial
    /// "set the environment variables" hint instead of a partial-configuration error.
    /// Never reveals the credential values.
    /// </summary>
    public string? ConfigurationIssue
    {
        get
        {
            if (IsConfigured) return null;
            var hasUsername = !string.IsNullOrWhiteSpace(_username);
            var hasPassword = _password.Length > 0;
            if (!hasUsername && !hasPassword) return null;
            if (!hasUsername) return "A password is set but MOMENTFERRY_USERNAME is empty.";
            if (_username.Length > 256) return "The configured username is longer than 256 characters.";
            if (!hasPassword) return "A username is set but MOMENTFERRY_PASSWORD is empty.";
            if (_password.Length < 12) return "The configured password must contain at least 12 characters.";
            return "The configured password is longer than 1024 characters.";
        }
    }

    public bool Matches(string? username, string? password) =>
        IsConfigured && SecureEquals(username, _username) & SecureEquals(password, _password);

    public ClaimsPrincipal CreatePrincipal()
    {
        PruneSessions();
        if (_sessions.Count >= 100) _sessions.Clear();
        var sessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _sessions[sessionId] = DateTimeOffset.UtcNow.AddHours(12);
        return new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, _username),
            new Claim(ClaimTypes.Name, _username),
            new Claim(SessionIdClaim, sessionId)
        ], AuthenticationScheme));
    }

    public bool HasValidSession(ClaimsPrincipal principal)
    {
        var sessionId = principal.FindFirstValue(SessionIdClaim);
        if (principal.Identity?.IsAuthenticated != true || sessionId is null ||
            !_sessions.TryGetValue(sessionId, out var expiresAt) || expiresAt <= DateTimeOffset.UtcNow)
            return false;

        _sessions[sessionId] = DateTimeOffset.UtcNow.AddHours(12);
        return true;
    }

    public void RevokeSession(ClaimsPrincipal principal)
    {
        var sessionId = principal.FindFirstValue(SessionIdClaim);
        if (sessionId is not null) _sessions.TryRemove(sessionId, out _);
    }

    public void RevokeAllSessions() => _sessions.Clear();

    private void PruneSessions()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var session in _sessions.Where(x => x.Value <= now))
            _sessions.TryRemove(session.Key, out _);
    }

    private static bool SecureEquals(string? supplied, string expected)
    {
        if (supplied is null || supplied.Length > 1024) return false;
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(suppliedHash, expectedHash);
    }
}

public sealed class PasswordProtectionMiddleware(
    RequestDelegate next,
    PasswordProtectionOptions options)
{
    public async Task InvokeAsync(HttpContext context, IRuntimeSettingsStore settingsStore)
    {
        var settings = await settingsStore.GetAsync(context.RequestAborted);
        if (settings.PasswordProtectionEnabled)
            context.Response.Headers.CacheControl = "no-store";

        if (!settings.PasswordProtectionEnabled || IsPublicPath(context.Request.Path))
        {
            await next(context);
            return;
        }

        if (!options.HasValidSession(context.User))
        {
            if (context.User.Identity?.IsAuthenticated == true)
                await context.SignOutAsync(PasswordProtectionOptions.AuthenticationScheme);

            await RejectUnauthenticatedAsync(context);
            return;
        }

        if (RequiresCsrfHeader(context.Request) &&
            !context.Request.Headers.ContainsKey(PasswordProtectionOptions.CsrfHeader))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "The required same-origin request header is missing." });
            return;
        }

        await next(context);
    }

    private static bool IsPublicPath(PathString path) =>
        path == "/health" ||
        path == "/login.html" ||
        path == "/login.js" ||
        path == "/styles.css" ||
        path == "/favicon.svg" ||
        path == "/i18n.js" ||
        path.StartsWithSegments("/i18n") ||
        path == "/api/v1/auth/status" ||
        path == "/api/v1/auth/login";

    private static bool RequiresCsrfHeader(HttpRequest request) =>
        request.Path.StartsWithSegments("/api") &&
        !HttpMethods.IsGet(request.Method) &&
        !HttpMethods.IsHead(request.Method) &&
        !HttpMethods.IsOptions(request.Method);

    private static async Task RejectUnauthenticatedAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/api") ||
            context.Request.Path.StartsWithSegments("/openapi") ||
            context.Request.Path == "/metrics")
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Authentication required." });
            return;
        }

        var returnUrl = context.Request.PathBase + context.Request.Path + context.Request.QueryString;
        context.Response.Redirect($"/login.html?returnUrl={Uri.EscapeDataString(returnUrl)}");
    }
}

public static class PasswordProtectionEndpoints
{
    public static IEndpointRouteBuilder MapPasswordProtectionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/auth/status", async (
            HttpContext context,
            IRuntimeSettingsStore settingsStore,
            PasswordProtectionOptions options,
            CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var settings = await settingsStore.GetAsync(ct);
            return Results.Ok(new
            {
                protectionEnabled = settings.PasswordProtectionEnabled,
                credentialsConfigured = options.IsConfigured,
                credentialsIssue = options.ConfigurationIssue,
                authenticated = options.HasValidSession(context.User)
            });
        });

        app.MapPost("/api/v1/auth/login", async (
            LoginRequest request,
            HttpContext context,
            IRuntimeSettingsStore settingsStore,
            PasswordProtectionOptions options,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!context.Request.Headers.ContainsKey(PasswordProtectionOptions.CsrfHeader))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            var settings = await settingsStore.GetAsync(ct);
            if (!settings.PasswordProtectionEnabled)
                return Results.Conflict(new { error = "Access protection is disabled." });
            if (!options.IsConfigured)
                return Results.Problem(
                    title: "Access protection is not configured.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            var logger = loggerFactory.CreateLogger("MomentFerry.Authentication");
            if (!options.Matches(request.Username, request.Password))
            {
                logger.LogInformation("Rejected sign-in attempt from {RemoteAddress}", context.Connection.RemoteIpAddress);
                return Results.Json(
                    new { error = "Invalid username or password." },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            await context.SignInAsync(
                PasswordProtectionOptions.AuthenticationScheme,
                options.CreatePrincipal());
            logger.LogInformation("User signed in from {RemoteAddress}", context.Connection.RemoteIpAddress);
            return Results.Ok(new { authenticated = true });
        })
            .RequireRateLimiting(PasswordProtectionOptions.LoginRateLimitPolicy)
            .WithMetadata(new RequestSizeLimitAttribute(4096));

        app.MapPost("/api/v1/auth/logout", async (
            HttpContext context,
            PasswordProtectionOptions options,
            ILoggerFactory loggerFactory) =>
        {
            options.RevokeSession(context.User);
            await context.SignOutAsync(PasswordProtectionOptions.AuthenticationScheme);
            loggerFactory.CreateLogger("MomentFerry.Authentication")
                .LogInformation("User signed out from {RemoteAddress}", context.Connection.RemoteIpAddress);
            return Results.NoContent();
        });

        return app;
    }
}

public sealed record LoginRequest(string? Username, string? Password);
