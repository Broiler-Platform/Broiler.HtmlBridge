using System.Text;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The CSSOM <c>CSS</c> namespace object — <c>CSS.supports()</c> and <c>CSS.escape()</c>.
/// <para>
/// The object did not exist at all, and its absence is not the mild kind: an unqualified
/// <c>CSS.supports(…)</c> is a <c>ReferenceError</c>, which aborts the entire calling script
/// rather than the one line. google.com's main bundle calls it unguarded, and it is where that
/// bundle stopped once <c>AbortSignal</c> let it get that far.
/// </para>
/// </summary>
internal static class CssBinding
{
    /// <summary>Builds the <c>CSS</c> object. Shared between <c>window.CSS</c> and the global.</summary>
    public static JSObject Build()
    {
        var css = new JSObject();

        css.FastAddValue(
            "supports",
            new DomFunction(Supports, "supports", 2),
            JSPropertyAttributes.EnumerableConfigurableValue);

        css.FastAddValue(
            "escape",
            new DomFunction(Escape, "escape", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        return css;
    }

    /// <summary>
    /// <c>CSS.supports(property, value)</c> and the single-argument
    /// <c>CSS.supports(conditionText)</c> form.
    /// </summary>
    /// <remarks>
    /// The answer comes from the CSS engine's own <c>@supports</c> evaluator, so a page gets one
    /// consistent answer whether it asks through this method or writes the <c>@supports</c> rule.
    /// <para>
    /// Deliberately not implemented by round-tripping the declaration through a detached
    /// element's <c>style</c>, which is the usual way to write this: Broiler's CSSOM stores what
    /// it is given without validating, so <c>totally-bogus-prop</c> survives the round trip and
    /// that technique would answer "supported" to everything. Claiming support for a feature the
    /// engine cannot honour is worse than admitting the gap — a page told that
    /// <c>animation-timing-function: linear(0, 1)</c> works emits an easing function that then
    /// renders as nothing, whereas a page told it does not takes the fallback it already carries.
    /// </para>
    /// </remarks>
    private static JSValue Supports(in Arguments arguments)
    {
        var condition = arguments.Length >= 2
            // The two-argument form takes a property and a value that are *not* re-parsed as a
            // condition, so a value containing its own parentheses — linear(0, 1) — composes
            // correctly rather than closing the query early.
            ? BuildDeclarationCondition(arguments[0].ToString(), arguments[1].ToString())
            : arguments.Length == 1
                ? arguments[0].ToString()
                : null;

        return condition is null ? JSBoolean.False : Evaluate(condition) ? JSBoolean.True : JSBoolean.False;
    }

    private static string BuildDeclarationCondition(string property, string value) =>
        "(" + property + ":" + value + ")";

    /// <summary>
    /// Defers to the CSS engine's <c>@supports</c> evaluator — the same grammar and the same
    /// feature-support oracle the cascade applies to an <c>@supports</c> prelude.
    /// </summary>
    /// <remarks>
    /// This was reached by name for a while, because the evaluator arrived as a patch against
    /// <c>Broiler.CSS</c> rather than as a pointer bump landing with this file, and a direct call
    /// would not have compiled until it was applied. It is in the pinned pointer now, so the
    /// lookup is gone and so is the conservative false it fell back to.
    /// </remarks>
    private static bool Evaluate(string condition) =>
        CSS.Dom.CssStyleEngine.EvaluatesSupportsCondition(condition);

    /// <summary>
    /// <c>CSS.escape(value)</c> — CSSOM §2, escaping a string so it can be used as a CSS
    /// identifier. A pure algorithm with no engine dependency, so it is exact rather than
    /// approximate.
    /// </summary>
    private static JSValue Escape(in Arguments arguments)
    {
        if (arguments.Length == 0)
            return new JSString(string.Empty);

        var value = arguments[0].ToString();
        var result = new StringBuilder(value.Length);

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];

            // NULL becomes the replacement character rather than being escaped or dropped.
            if (c == '\0')
            {
                result.Append('�');
                continue;
            }

            // Control characters and DELETE, plus a leading digit — and a digit in second place
            // when the first character is a hyphen — are written as a hexadecimal escape.
            if (c is >= '' and <= '' or ''
                || (i == 0 && c is >= '0' and <= '9')
                || (i == 1 && c is >= '0' and <= '9' && value[0] == '-'))
            {
                result.Append('\\').Append(((int)c).ToString("x")).Append(' ');
                continue;
            }

            // A lone leading hyphen has to be escaped; "--" does not.
            if (i == 0 && c == '-' && value.Length == 1)
            {
                result.Append('\\').Append(c);
                continue;
            }

            // Everything that may appear in an identifier unescaped: ASCII alphanumerics, the
            // hyphen and underscore, and anything above ASCII.
            if (c >= '' || c == '-' || c == '_'
                || c is >= '0' and <= '9'
                || c is >= 'A' and <= 'Z'
                || c is >= 'a' and <= 'z')
            {
                result.Append(c);
                continue;
            }

            result.Append('\\').Append(c);
        }

        return new JSString(result.ToString());
    }
}
