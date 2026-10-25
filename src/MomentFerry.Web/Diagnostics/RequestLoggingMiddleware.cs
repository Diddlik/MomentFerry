using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace MomentFerry.Web.Diagnostics;

/// <summary>
/// One line for every API request that changed something, failed, ran long or was abandoned. Reads
/// stay at Debug: the console polls its status every few seconds, and logging those would push the
/// routing and transfer records out of the activity log.
///
/// An abandoned request gets a warning of its own. A reverse proxy that gives up answers the browser
/// with 504 by itself, so without this line the server would carry no trace of it, and the work that
/// request was doing has been cancelled with it.
/// </summary>
public sealed class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
{
    private static readonly TimeSpan SlowRequest = TimeSpan.FromSeconds(5);

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await next(context);
            return;
        }

        var started = Stopwatch.GetTimestamp();
        // Endpoints answer a refusal with a JSON reason. Keeping the start of every response body lets
        // that reason reach the log without each endpoint having to log it separately.
        var body = new HeadCapturingStream(context.Response.Body);
        context.Response.Body = body;
        Exception? failure = null;
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            context.Response.Body = body.Inner;
            Log(context, Stopwatch.GetElapsedTime(started), failure, body.Head);
        }
    }

    private void Log(HttpContext context, TimeSpan elapsed, Exception? failure, string head)
    {
        var method = context.Request.Method;
        // The query string is left out on purpose: folder browsing carries host paths in it.
        var path = context.Request.Path.Value;

        if (context.RequestAborted.IsCancellationRequested)
        {
            logger.LogWarning(
                "{Method} {Path}: the client disconnected after {Seconds:F1}s and the request was cancelled. " +
                "Behind a reverse proxy this is usually its timeout, and the browser then shows 504",
                method,
                path,
                elapsed.TotalSeconds);
            return;
        }

        if (failure is not null)
        {
            logger.LogError(failure, "{Method} {Path} failed after {Milliseconds:F0} ms", method, path, elapsed.TotalMilliseconds);
            return;
        }

        var status = context.Response.StatusCode;
        var level = status switch
        {
            >= 500 => LogLevel.Error,
            // An expired session makes every poll answer 401 until the page signs in again.
            StatusCodes.Status401Unauthorized => LogLevel.Debug,
            >= 400 => LogLevel.Warning,
            // A template preview is a POST only to carry its body; it reads, and fires on every keystroke.
            _ when HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || path?.EndsWith("/preview", StringComparison.Ordinal) == true =>
                elapsed >= SlowRequest ? LogLevel.Information : LogLevel.Debug,
            _ => LogLevel.Information
        };
        if (status >= 400 && Reason(head) is { } reason)
        {
            logger.Log(level, "{Method} {Path} returned {Status} in {Milliseconds:F0} ms: {Reason}", method, path, status, elapsed.TotalMilliseconds, reason);
            return;
        }

        logger.Log(level, "{Method} {Path} returned {Status} in {Milliseconds:F0} ms", method, path, status, elapsed.TotalMilliseconds);
    }

    /// <summary>The <c>error</c>, <c>detail</c> or <c>title</c> of a JSON error body, else its raw text.</summary>
    private static string? Reason(string head)
    {
        if (string.IsNullOrWhiteSpace(head)) return null;
        try
        {
            using var json = JsonDocument.Parse(head);
            foreach (var name in new[] { "error", "detail", "title" })
            {
                if (json.RootElement.ValueKind == JsonValueKind.Object &&
                    json.RootElement.TryGetProperty(name, out var value) &&
                    value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON, or cut off at the capture limit: the raw start of the body still says something.
        }

        return head.Trim();
    }

    /// <summary>Passes every write through unchanged and keeps the first bytes of the body.</summary>
    private sealed class HeadCapturingStream(Stream inner) : Stream
    {
        private const int Limit = 1024;
        private readonly MemoryStream _head = new();

        public Stream Inner { get; } = inner;
        public string Head => Encoding.UTF8.GetString(_head.GetBuffer(), 0, (int)_head.Length);

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Capture(buffer);
            Inner.Write(buffer);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Capture(buffer.Span);
            return Inner.WriteAsync(buffer, cancellationToken);
        }

        public override void Flush() => Inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => Inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        private void Capture(ReadOnlySpan<byte> buffer)
        {
            var room = Limit - (int)_head.Length;
            if (room > 0) _head.Write(buffer[..Math.Min(room, buffer.Length)]);
        }
    }
}
