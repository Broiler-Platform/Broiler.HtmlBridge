using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Broiler.HtmlBridge.Logging;

/// <summary>
/// Identifies the subsystem that produced a log entry.
/// </summary>
public enum LogCategory
{
    /// <summary>Issues originating from the HTML-Renderer CSS/HTML pipeline.</summary>
    HtmlRenderer,

    /// <summary>Issues originating from the YantraJS JavaScript engine.</summary>
    JavaScript,
}

/// <summary>
/// Severity level for a log entry.
/// </summary>
public enum LogLevel
{
    /// <summary>Verbose diagnostic information.</summary>
    Debug,

    /// <summary>General informational messages.</summary>
    Info,

    /// <summary>Potential issues that do not prevent operation.</summary>
    Warning,

    /// <summary>Errors that affect operation but allow recovery.</summary>
    Error,
}

/// <summary>
/// A single structured log entry produced by the rendering pipeline.
/// </summary>
public sealed class RenderLogEntry
{
    /// <summary>UTC timestamp when the entry was created.</summary>
    public DateTime Timestamp { get; init; }

    /// <summary>The subsystem that produced this entry.</summary>
    public LogCategory Category { get; init; }

    /// <summary>Severity level.</summary>
    public LogLevel Level { get; init; }

    /// <summary>The specific feature or context within the subsystem (e.g. "ScriptEngine", "DomBridge.fetch").</summary>
    public string Context { get; init; } = string.Empty;

    /// <summary>Human-readable message describing the event.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>The exception associated with this entry, if any.</summary>
    public Exception? Exception { get; init; }

    /// <inheritdoc />
    public override string ToString()
    {
        var ex = Exception != null ? $" | {Exception.GetType().Name}: {Exception.Message}" : string.Empty;
        return $"[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level}] [{Category}/{Context}] {Message}{ex}";
    }
}

/// <summary>
/// Thread-safe, in-memory logger for the rendering pipeline.  All entries
/// are captured in a list that can be inspected by callers (tests, UI,
/// CLI diagnostics).  Entries are also forwarded to
/// <see cref="Debug.WriteLine(string)"/> so that existing diagnostics
/// workflows are preserved.
/// </summary>
public static class RenderLogger
{
    private static readonly List<RenderLogEntry> _entries = [];
    private static readonly Lock _lock = new();
    private static LogLevel _minimumLevel = LogLevel.Debug;

    /// <summary>
    /// Raised whenever a new log entry is recorded.  Subscribers receive the
    /// entry synchronously on the logging thread – handlers should return
    /// quickly to avoid blocking the rendering pipeline.
    /// </summary>
    public static event Action<RenderLogEntry>? EntryLogged;

    /// <summary>
    /// Gets or sets the minimum severity level. Entries below this level
    /// are discarded. Default is <see cref="LogLevel.Debug"/> (capture
    /// everything).
    /// </summary>
    public static LogLevel MinimumLevel
    {
        get { lock (_lock) return _minimumLevel; }
        set { lock (_lock) _minimumLevel = value; }
    }

    /// <summary>
    /// Removes all previously captured entries.
    /// </summary>
    public static void Clear()
    {
        lock (_lock) _entries.Clear();
    }

    /// <summary>
    /// Log an entry at the specified level.
    /// </summary>
    public static void Log(LogCategory category, LogLevel level, string context, string message, Exception? exception = null)
    {
        if (level < _minimumLevel)
            return;

        var entry = new RenderLogEntry
        {
            Timestamp = DateTime.UtcNow,
            Category = category,
            Level = level,
            Context = context,
            Message = message,
            Exception = exception,
        };

        lock (_lock) _entries.Add(entry);
        Debug.WriteLine(entry.ToString());
        EntryLogged?.Invoke(entry);
    }

    /// <summary>
    /// Convenience method to log a <see cref="LogLevel.Debug"/> message.
    /// </summary>
    public static void LogDebug(LogCategory category, string context, string message)
        => Log(category, LogLevel.Debug, context, message);

    /// <summary>
    /// Convenience method to log a <see cref="LogLevel.Warning"/> message.
    /// </summary>
    public static void LogWarning(LogCategory category, string context, string message, Exception? exception = null)
        => Log(category, LogLevel.Warning, context, message, exception);

    /// <summary>
    /// Convenience method to log an <see cref="LogLevel.Error"/> with an exception.
    /// </summary>
    public static void LogError(LogCategory category, string context, string message, Exception exception)
        => Log(category, LogLevel.Error, context, message, exception);
}
