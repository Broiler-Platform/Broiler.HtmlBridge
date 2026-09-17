using System.Text.RegularExpressions;
using Broiler.Dom.Html;

namespace Broiler.HtmlBridge.Dom;

/// <summary>
/// The one repair the raw-source attribute readers make before handing markup to the shared
/// <see cref="HtmlTokenizer"/>: <c>HtmlBaseHref.TryFindBaseHref</c> and
/// <c>ContentSecurityPolicy.ExtractNonceFromAttributes</c> read a single attribute out of markup that has
/// no DOM yet, and must not lose a value the regular expressions they replaced read correctly.
/// </summary>
internal static class HtmlSourceAttributes
{
    /// <summary>
    /// <paramref name="source"/> with the whitespace removed between an attribute named
    /// <paramref name="attributeName"/>, spelled in any case, and the <c>=</c> that follows it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// HTML §13.2.5.34, the after-attribute-name state, skips whitespace before the <c>=</c>, so
    /// <c>href = "x/"</c> gives <c>href</c> the value <c>x/</c>. The consumed tokenizer (Broiler.Dom.Html
    /// 0.1.0-preview.2, the BeforeAttributeName state) instead commits the attribute with an empty value
    /// when it meets that <c>=</c>, then reads the value under an empty name and drops it — and the DOM the
    /// bridge builds holds the same empty value. The regular expressions the two readers replaced
    /// tolerated the whitespace, so without this the swap to the tokenizer gave up an answer that was
    /// already the Standard's. It stays until the tokenizer is fixed upstream.
    /// </para>
    /// <para>
    /// Only whitespace is removed — never a quote, <c>&lt;</c> or <c>&gt;</c> — and only after the name
    /// where an attribute name can begin: at the start of the text, or after whitespace, a quote or a
    /// <c>/</c>. So every start tag, comment and raw-text run still ends where it did, and the name spelled
    /// anywhere else (in text, a comment, raw text or another attribute's value) changes only that text,
    /// which neither reader looks at. The value it can change is the attribute's own, when that value
    /// itself spells the name followed by a spaced <c>=</c>.
    /// </para>
    /// </remarks>
    public static string CloseSpaceBeforeEquals(string source, string attributeName) =>
        Regex.Replace(
            source,
            $@"(?<![^\s""'/])({Regex.Escape(attributeName)})\s+=",
            "$1=",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
