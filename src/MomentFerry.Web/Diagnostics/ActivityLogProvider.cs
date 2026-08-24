namespace MomentFerry.Web.Diagnostics;

/// <summary>
/// Feeds MomentFerry's own log output into <see cref="ActivityLog"/>. Framework categories are left
/// out: request logging would push the routing and transfer records out of the ring within seconds.
/// </summary>
[ProviderAlias("Activity")]
public sealed class ActivityLogProvider(ActivityLog log) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) =>
        categoryName.StartsWith("MomentFerry", StringComparison.Ordinal)
            ? new ActivityLogger(log, categoryName)
            : NullLogger.Instance;

    public void Dispose() { }

    private sealed class ActivityLogger(ActivityLog log, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var message = formatter(state, exception);
            if (exception is not null) message = $"{message} — {exception.Message}";
            log.Add(DateTimeOffset.UtcNow, logLevel, category, message);
        }
    }

    private sealed class NullLogger : ILogger
    {
        public static readonly NullLogger Instance = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}
