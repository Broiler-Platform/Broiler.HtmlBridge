using System;
using System.Collections.Generic;

namespace Broiler.HtmlBridge;

/// <summary>
/// The HTML spec's classification of a <c>&lt;script&gt;</c> element's <c>type</c> attribute:
/// classic script, module, or neither — where "neither" means the element is a data block the
/// browser must never execute.
/// </summary>
/// <remarks>
/// Internal on purpose: this is how the bridge decides what to run, not something a host chooses.
/// It is visible to Broiler.Cli, Broiler.HtmlBridge.Dom and the test assemblies through the
/// InternalsVisibleTo grants Broiler.HtmlBridge.Core already carries.
/// </remarks>
/// <remarks>
/// <para>
/// Parking data in a <c>&lt;script&gt;</c> is the standard way to ship it inside a document, because
/// the HTML parser treats the element's content as raw text and a browser leaves it alone: JSON-LD
/// (<c>application/ld+json</c>), framework state (<c>application/json</c>), speculation rules,
/// import maps, and client-side templates (<c>text/template</c>,
/// <c>text/x-handlebars-template</c>) are all delivered this way. Handing such a block to the
/// JavaScript engine produces a compile error for content that was never meant to compile — and,
/// because the block usually is not JavaScript at all, an error that describes nothing recognisable:
/// a template starting with a bullet or a dash fails as "Unexpected token Empty:  at 1, 1".
/// </para>
/// <para>
/// This is not a rare shape. Across the front pages of eight widely-read sites, 200 of the 358
/// <c>&lt;script&gt;</c> elements carried a non-JavaScript type — <c>application/ld+json</c> alone
/// was the single most common kind of script element on the sample, ahead of scripts with no type
/// attribute at all.
/// </para>
/// </remarks>
internal static class ScriptMimeType
{
    /// <summary>The JavaScript MIME type essences, including the legacy aliases: pages in the wild
    /// still ship <c>text/jscript</c> and the versioned <c>text/javascript1.x</c>.</summary>
    private static readonly HashSet<string> JavaScriptMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/ecmascript",
        "application/javascript",
        "application/x-ecmascript",
        "application/x-javascript",
        "text/ecmascript",
        "text/javascript",
        "text/javascript1.0",
        "text/javascript1.1",
        "text/javascript1.2",
        "text/javascript1.3",
        "text/javascript1.4",
        "text/javascript1.5",
        "text/jscript",
        "text/livescript",
        "text/x-ecmascript",
        "text/x-javascript",
    };

    /// <summary>
    /// Whether <paramref name="type"/> selects a classic script. An absent, empty or whitespace-only
    /// attribute means yes; anything else must be a JavaScript MIME essence, which rules out both
    /// <c>module</c> (not a classic script) and every data-block type.
    /// </summary>
    internal static bool IsClassicScript(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return true;

        // "text/javascript; charset=utf-8" — the essence is the part before the parameters.
        var essence = type.Split(';')[0].Trim();
        return JavaScriptMimeTypes.Contains(essence);
    }

    /// <summary>Whether <paramref name="type"/> selects a module script (<c>type="module"</c>,
    /// matched case-insensitively and with surrounding whitespace ignored, and taking no
    /// parameters).</summary>
    internal static bool IsModule(string? type)
        => type != null && string.Equals(type.Trim(), "module", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a <c>&lt;script&gt;</c> with this <c>type</c> is executed at all — a classic script
    /// or a module. False for every data block, which is the case callers must skip rather than
    /// hand to the engine.
    /// </summary>
    internal static bool IsExecutable(string? type) => IsClassicScript(type) || IsModule(type);
}
