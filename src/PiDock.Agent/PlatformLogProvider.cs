using System.Text.Json;
using Microsoft.Extensions.Logging;
using PiDock.Core;

namespace PiDock.Agent;

// Uses the same bounded journal as app log ingestion; it never logs its own persistence failures.
public sealed class PlatformLogProvider(SharedApplicationServices services) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new PlatformLogger(categoryName, services);
    public void Dispose() { }
    private sealed class PlatformLogger(string category, SharedApplicationServices services) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => category.StartsWith("PiDock.", StringComparison.Ordinal) && level >= LogLevel.Information && level < LogLevel.None;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            try
            {
                var message = formatter(state, exception);
                if (message.Length > 2048) message = message[..2048];
                services.Log("pidockos", new(1, Guid.NewGuid(), "host-log", DateTimeOffset.UtcNow, level.ToString(),
                    level == LogLevel.Critical ? OperationalPriority.Critical : null, message, null,
                    new() { ["category"] = JsonSerializer.SerializeToElement(category), ["eventId"] = JsonSerializer.SerializeToElement(eventId.Id) }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PlatformCapacityException or PlatformConflictException)
            { Interlocked.Increment(ref dropped); }
        }
    }
    private static long dropped;
    public static long Dropped => Interlocked.Read(ref dropped);
}
