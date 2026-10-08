using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ImdbRatings.Tests;

internal sealed class RecordingLogger<T>(bool debugEnabled = true) : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Messages { get; } = new();

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.Debug || debugEnabled;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Messages.Add((logLevel, formatter(state, exception)));
}
