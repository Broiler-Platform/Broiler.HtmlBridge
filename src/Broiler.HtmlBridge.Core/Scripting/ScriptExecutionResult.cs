using System.Collections.Generic;

namespace Broiler.HtmlBridge.Scripting;

/// <summary>
/// Detailed result of a JavaScript execution batch, including per-script
/// error information and stack traces.
/// </summary>
public sealed class ScriptExecutionResult
{
    /// <summary>
    /// Whether all scripts completed without error.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Per-script errors captured during execution. Empty when all
    /// scripts succeed.
    /// </summary>
    public IReadOnlyList<ScriptError> Errors { get; init; } = [];
}

/// <summary>
/// Describes a single JavaScript error captured during execution.
/// </summary>
public sealed class ScriptError
{
    /// <summary>Zero-based index of the script that failed.</summary>
    public int ScriptIndex { get; init; }

    /// <summary>The error message.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// The .NET stack trace captured at the point of failure.
    /// May contain YantraJS internal frames.
    /// </summary>
    public string StackTrace { get; init; } = string.Empty;
}
