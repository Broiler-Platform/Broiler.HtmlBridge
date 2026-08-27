using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The class-name set matching shared by both halves of <c>getElementsByClassName</c> — the document
/// one in <see cref="DocumentQueryBinding"/> and the element one behind
/// <see cref="SelectorsBinding.GetElementsByClassName"/>.
/// </summary>
/// <remarks>
/// <para>
/// It lives in one place because the two halves had already drifted: the document half read its
/// argument as a single class name, so <c>getElementsByClassName("a b")</c> found only elements
/// whose whole class attribute was the literal <c>"a b"</c> — that is, nothing — where DOM §4.5 asks
/// for the elements carrying <em>both</em> classes. Sharing the rule is what keeps the answer the
/// same whichever half a page reaches through.
/// </para>
/// <para>
/// Both the argument and the element's <c>class</c> attribute are ordered sets of tokens split on
/// ASCII whitespace, and comparison is ordinal because class identity is case-sensitive in a
/// standards-mode document. An empty set matches nothing rather than everything.
/// </para>
/// </remarks>
internal static class ClassNameSet
{
    /// <summary>Splits the requested class names. An empty result matches no element.</summary>
    public static string[] Parse(string? classNames) =>
        (classNames ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Whether <paramref name="element"/> carries every name in <paramref name="wanted"/>.</summary>
    public static bool Matches(DomElement element, string[] wanted)
    {
        if (wanted.Length == 0)
            return false;

        // Both sides are a handful of short tokens, so a linear scan beats hashing them per element —
        // and the overwhelmingly common call asks for exactly one class.
        var classes = (element.ClassName ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (classes.Length == 0)
            return false;

        foreach (var name in wanted)
        {
            if (Array.IndexOf(classes, name) < 0)
                return false;
        }

        return true;
    }
}
