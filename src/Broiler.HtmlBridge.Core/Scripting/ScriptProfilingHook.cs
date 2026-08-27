using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Broiler.HtmlBridge.Scripting;

/// <summary>
/// Records the execution time of individual JavaScript scripts to support
/// performance profiling.  Each invocation of
/// <see cref="Measure(string, Action)"/> captures a
/// <see cref="ScriptTimingEntry"/>.
/// </summary>
public sealed class ScriptProfilingHook
{
    private readonly List<ScriptTimingEntry> _entries = [];

    /// <summary>
    /// All recorded timing entries.
    /// </summary>
    public IReadOnlyList<ScriptTimingEntry> Entries => _entries;

    /// <summary>
    /// Execute <paramref name="action"/> (typically a script evaluation)
    /// and record how long it takes.
    /// </summary>
    /// <param name="scriptLabel">
    /// A human-readable label (e.g. a filename or <c>"inline-0"</c>).
    /// </param>
    /// <param name="action">The work to time.</param>
    public void Measure(string scriptLabel, Action action)
    {
        var sw = Stopwatch.StartNew();
        Exception? caughtException = null;
        try
        {
            action();
        }
        catch (Exception ex)
        {
            caughtException = ex;
            throw;
        }
        finally
        {
            sw.Stop();
            _entries.Add(new ScriptTimingEntry
            {
                Label = scriptLabel,
                Elapsed = sw.Elapsed,
                Succeeded = caughtException == null
            });
        }
    }

    /// <summary>
    /// Remove all recorded entries.
    /// </summary>
    public void Clear() => _entries.Clear();
}

/// <summary>
/// One timing measurement for a single script execution.
/// </summary>
public sealed class ScriptTimingEntry
{
    /// <summary>Human-readable label for the script.</summary>
    public required string Label { get; init; }

    /// <summary>Wall-clock time spent executing the script.</summary>
    public required TimeSpan Elapsed { get; init; }

    /// <summary>Whether the script completed without throwing.</summary>
    public required bool Succeeded { get; init; }
}
