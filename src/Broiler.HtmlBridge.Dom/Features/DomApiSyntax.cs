using System.Text.RegularExpressions;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The two syntax checks a DOM method must perform on its own argument before it does anything —
/// the attribute name <c>setAttribute</c> takes (DOM §4.9.1) and the selector
/// <c>querySelector</c> and friends take (DOM §4.2.6). Both are specified to throw rather than to
/// degrade: an invalid attribute name is an <c>InvalidCharacterError</c> and an unparsable selector
/// is a <c>SyntaxError</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these live at the bridge and not in the canonical components.</b> The obvious homes —
/// <c>Broiler.Dom</c>'s <c>DomElement.SetAttribute</c> and <c>Broiler.CSS.Dom</c>'s
/// <c>CssSelectorMatcher.Matches</c> — are exactly where they must <em>not</em> go, and for the same
/// reason in both cases: those are the paths the parser and the cascade use, and both are required to
/// be lenient. The HTML parser calls <c>SetAttribute</c> with whatever the document contains, and HTML
/// permits attribute names the XML <c>Name</c> production rejects; a stylesheet rule whose selector
/// does not parse is dropped per CSS error handling, not fatal, which is what the matcher returning
/// <see langword="false"/> expresses. Throwing in either place would break the layer that has to
/// tolerate bad input. The requirement is a property of the <em>scripted DOM API</em>, so it belongs
/// at the API boundary, which is the bridge — the same place the <c>DOMException</c> is minted.
/// </para>
/// <para>
/// Every expectation below was measured against Chromium rather than reasoned from the grammar; the
/// two divergences that remain are recorded on <see cref="IsValidSelectorList"/> and are deliberate.
/// </para>
/// </remarks>
internal static partial class DomApiSyntax
{
    /// <summary>
    /// Whether <paramref name="name"/> matches the XML <c>Name</c> production, which is what DOM
    /// §4.9.1 requires of <c>setAttribute</c>'s qualified name.
    /// </summary>
    /// <remarks>
    /// Colons are allowed — here and, deliberately, unlike
    /// <c>DomNameValidation.ValidateElementName</c>. <c>Name</c> admits <c>:</c> as both a start and a
    /// body character, so <c>setAttribute('xlink:href', …)</c>, <c>setAttribute('v-on:click', …)</c>
    /// and even <c>setAttribute('a:b:c', …)</c> are valid and a browser accepts all three; the
    /// namespace rules that make a colon meaningful apply to <c>setAttributeNS</c>, not here. Getting
    /// this wrong in the strict direction would have been the damaging outcome: rejecting
    /// <c>xlink:href</c> breaks inline SVG, which is why it is pinned by a regression rather than
    /// left to the pattern.
    /// <para>
    /// The character classes follow the approximation <c>DomNameValidation</c> already uses —
    /// Unicode categories rather than the production's literal code-point ranges — so a non-ASCII
    /// name such as <c>aé</c> is accepted, as Chromium accepts it.
    /// </para>
    /// </remarks>
    public static bool IsValidAttributeName(string? name) =>
        !string.IsNullOrEmpty(name) && !name.Contains('\0') && AttributeNamePattern().IsMatch(name);

    // Fully qualified: `Broiler.Regex` is a namespace in this solution, so a bare `Regex` inside
    // `Broiler.HtmlBridge.Dom.Features` binds to it rather than to the BCL type.
    [GeneratedRegex(@"^[\p{L}_:][\p{L}\p{N}_.\-:]*$", RegexOptions.Compiled)]
    private static partial System.Text.RegularExpressions.Regex AttributeNamePattern();

    /// <summary>
    /// Whether <paramref name="selector"/> parses as a selector list, which is what DOM §4.2.6
    /// requires of <c>querySelector</c>, <c>querySelectorAll</c>, <c>matches</c> and <c>closest</c>
    /// before any matching happens.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This validates <em>structure</em>: delimiters balance, every compound is non-empty, every
    /// combinator has operands on both sides, every <c>#</c>/<c>.</c>/<c>:</c> is followed by an
    /// identifier, and every attribute selector is a well-formed
    /// <c>[name]</c>/<c>[name op value]</c>. That is what the reported defect was made of, and the
    /// reason it mattered is that the failure was never a clean <c>null</c>: the lenient matcher read
    /// <c>div:::bogus</c> as <c>div</c> and matched a real element, and <c>[</c> matched four, so an
    /// invalid selector silently returned the <em>wrong</em> answer rather than no answer.
    /// </para>
    /// <para>
    /// <b>Two deliberate divergences, both in the permissive direction.</b> A well-formed but
    /// <em>unknown</em> pseudo-class or pseudo-element (<c>:nope</c>, <c>::bogus</c>,
    /// <c>::-moz-focus-inner</c>) is accepted here rather than rejected, where Chromium throws. That
    /// is on purpose: rejecting an unknown name means keeping a list of every pseudo this engine
    /// supports, and the list would have to be a superset of what pages use — Chromium itself accepts
    /// <c>:focus-visible</c>, <c>:defined</c>, <c>:modal</c>, <c>:popover-open</c>, <c>::marker</c>
    /// and <c>::-webkit-scrollbar</c> while rejecting <c>::-moz-focus-inner</c> and <c>:matches()</c>,
    /// so any list drifts. Turning an unknown name into a thrown exception would break a page that
    /// merely asked for a pseudo Broiler lacks; accepting is the failure mode that cannot regress a
    /// working page. The same reasoning covers a pseudo-class written after a pseudo-element
    /// (<c>::before:hover</c>), which Chromium rejects. The second divergence is the Selectors 4
    /// <c>s</c> case flag, which is valid per specification and which Chromium has not implemented.
    /// </para>
    /// <para>
    /// Accepting is not the same as matching, and the distinction is load-bearing: an unknown
    /// <em>pseudo-element</em> answers no element because <see cref="CarriesPseudoElement"/> settles
    /// it before the matcher runs, and an argument-less unknown pseudo-class already matches nothing.
    /// One shape still answers wrongly — an unknown pseudo-class <em>with</em> an argument, where the
    /// matcher's lenient default matches the first element. That is a matching bug in
    /// <c>Broiler.CSS.Dom</c> rather than a syntax one, so it is characterized rather than fixed here.
    /// </para>
    /// </remarks>
    public static bool IsValidSelectorList(string? selector)
    {
        if (selector is null)
            return false;

        var start = 0;
        var parens = 0;
        var brackets = 0;
        for (var index = 0; index < selector.Length; index++)
        {
            var character = selector[index];
            if (character == '\\')
            {
                index++;
                continue;
            }

            if (character is '"' or '\'')
            {
                if (!SkipString(selector, ref index))
                    return false;
                continue;
            }

            switch (character)
            {
                case '(':
                    parens++;
                    break;
                case ')' when --parens < 0:
                    return false;
                case '[':
                    brackets++;
                    break;
                case ']' when --brackets < 0:
                    return false;
                case ',' when parens == 0 && brackets == 0:
                    if (!IsValidComplexSelector(selector[start..index]))
                        return false;
                    start = index + 1;
                    break;
            }
        }

        return parens == 0 && brackets == 0 && IsValidComplexSelector(selector[start..]);
    }

    /// <summary>
    /// One complex selector: a compound, then any number of combinator-plus-compound pairs. A
    /// <paramref name="allowLeadingCombinator"/> selector is the relative form <c>:has()</c> takes,
    /// where <c>:has(&gt; p)</c> is legal and means a child rather than a syntax error.
    /// </summary>
    private static bool IsValidComplexSelector(string part, bool allowLeadingCombinator = false)
    {
        var selector = part.Trim(Whitespace);
        if (selector.Length == 0)
            return false;

        var index = 0;
        if (allowLeadingCombinator && selector[0] is '>' or '+' or '~')
        {
            index++;
            SkipWhitespace(selector, ref index);
            if (index >= selector.Length)
                return false;
        }

        while (true)
        {
            if (ConsumeCompound(selector, ref index, out var sawPseudoElement) <= 0)
                return false;

            var sawWhitespace = index < selector.Length && IsWhitespace(selector[index]);
            SkipWhitespace(selector, ref index);
            if (index >= selector.Length)
                return true;

            // A pseudo-element may only appear on the subject — the last compound. Anything after it
            // is a syntax error, so `div::before p` throws where `div::before` does not. Measured.
            if (sawPseudoElement)
                return false;

            if (selector[index] is '>' or '+' or '~')
            {
                index++;
                SkipWhitespace(selector, ref index);
                // A combinator with nothing after it ("div >") is as invalid as one with nothing
                // before it, which the leading-combinator guard above rejects.
                if (index >= selector.Length)
                    return false;
                continue;
            }

            // No combinator, so the only way this is still one complex selector is a descendant
            // combinator — real whitespace. Otherwise ConsumeCompound stopped on a character it
            // could not read, which is the "div@x" / "div!" shape.
            if (!sawWhitespace)
                return false;
        }
    }

    /// <summary>
    /// Whether <paramref name="selector"/> carries a pseudo-element, which makes it match nothing
    /// through the DOM API however well it parses.
    /// </summary>
    /// <remarks>
    /// A pseudo-element selects a box that is not an element, so there is no node for
    /// <c>querySelector</c> to return and none for <c>matches</c> to be true of: a browser answers
    /// <c>null</c>/<c>false</c> for <c>::before</c>, <c>::marker</c>, <c>::selection</c> and every
    /// other spelling, valid or not. Broiler's matcher strips the pseudo-element and matches what is
    /// left, so <c>querySelector('::before')</c> came back with the <c>&lt;html&gt;</c> element — the
    /// same "silently returns the wrong element" failure the selector validation above exists to
    /// stop, reached by a different route. This is a DOM-API rule only: the cascade must go on
    /// matching these rules, which is exactly what paints a <c>::before</c>.
    /// <para>
    /// The legacy one-colon spellings (<c>:before</c>, <c>:after</c>, <c>:first-line</c>,
    /// <c>:first-letter</c>) are pseudo-elements too and answer the same way.
    /// </para>
    /// </remarks>
    public static bool CarriesPseudoElement(string? selector)
    {
        if (string.IsNullOrEmpty(selector))
            return false;

        var index = 0;
        while (index < selector.Length)
        {
            if (selector[index] == '\\')
            {
                index += 2;
                continue;
            }

            if (selector[index] is '"' or '\'')
            {
                if (!SkipString(selector, ref index))
                    return false;
                index++;
                continue;
            }

            if (selector[index] != ':')
            {
                index++;
                continue;
            }

            var start = index;
            index++;
            var doubled = index < selector.Length && selector[index] == ':';
            if (doubled)
                index++;

            var nameStart = index;
            if (!ConsumeIdentifier(selector, ref index))
            {
                index = start + 1;
                continue;
            }

            if (doubled || LegacyPseudoElements.Contains(selector[nameStart..index].ToLowerInvariant()))
                return true;
        }

        return false;
    }

    /// <summary>The four pseudo-elements CSS 2.1 spelled with one colon, which is still valid.</summary>
    private static readonly HashSet<string> LegacyPseudoElements =
        ["before", "after", "first-line", "first-letter"];

    /// <summary>
    /// One compound selector, returning how many simple selectors it held: <c>0</c> for an empty
    /// compound and <c>-1</c> for a malformed one, both of which are failures.
    /// </summary>
    private static int ConsumeCompound(string selector, ref int index, out bool sawPseudoElement)
    {
        sawPseudoElement = false;
        var count = 0;
        while (index < selector.Length)
        {
            switch (selector[index])
            {
                case '*':
                    index++;
                    if (!ConsumeNamespaceSuffix(selector, ref index, prefixWasStar: true))
                        return -1;
                    count++;
                    continue;

                case '#':
                case '.':
                    index++;
                    if (!ConsumeIdentifier(selector, ref index))
                        return -1;
                    count++;
                    continue;

                case '[':
                    if (!ConsumeAttributeSelector(selector, ref index))
                        return -1;
                    count++;
                    continue;

                case ':':
                    if (!ConsumePseudo(selector, ref index, out var isPseudoElement))
                        return -1;
                    sawPseudoElement |= isPseudoElement;
                    count++;
                    continue;

                // A bare "|div" is the no-namespace form and is valid; a bare "|" is not.
                case '|':
                    index++;
                    if (!ConsumeIdentifierOrStar(selector, ref index))
                        return -1;
                    count++;
                    continue;

                default:
                    if (!IsIdentifierStart(selector[index]) && selector[index] != '\\')
                        return count;
                    if (!ConsumeIdentifier(selector, ref index))
                        return -1;
                    if (!ConsumeNamespaceSuffix(selector, ref index, prefixWasStar: false))
                        return -1;
                    count++;
                    continue;
            }
        }

        return count;
    }

    /// <summary>
    /// The <c>|name</c> half of a namespaced type selector, when one follows.
    /// </summary>
    /// <remarks>
    /// A <em>named</em> prefix is rejected. <c>querySelector</c> has no namespace declarations to
    /// resolve one against, so <c>svg|rect</c> and <c>a|*</c> are syntax errors in a browser while
    /// <c>*|a</c> and <c>|a</c> — the any-namespace and no-namespace forms, which need no declaration
    /// — are fine. Both were measured. The <c>|=</c> attribute operator is not a namespace separator,
    /// so it is stepped around rather than consumed here.
    /// </remarks>
    private static bool ConsumeNamespaceSuffix(string selector, ref int index, bool prefixWasStar)
    {
        if (index >= selector.Length || selector[index] != '|')
            return true;
        if (index + 1 < selector.Length && selector[index + 1] == '=')
            return true;
        if (!prefixWasStar)
            return false;

        index++;
        return ConsumeIdentifierOrStar(selector, ref index);
    }

    /// <summary>One <c>[…]</c> attribute selector.</summary>
    private static bool ConsumeAttributeSelector(string selector, ref int index)
    {
        index++; // past '['
        SkipWhitespace(selector, ref index);

        if (index < selector.Length && selector[index] == '*')
        {
            index++;
            if (index >= selector.Length || selector[index] != '|')
                return false;
            index++;
            if (!ConsumeIdentifierOrStar(selector, ref index))
                return false;
        }
        else
        {
            if (index < selector.Length && selector[index] == '|')
                index++;
            if (!ConsumeIdentifier(selector, ref index))
                return false;
        }

        SkipWhitespace(selector, ref index);
        if (index >= selector.Length)
            return false;
        if (selector[index] == ']')
        {
            index++;
            return true;
        }

        // ~= |= ^= $= *= or a bare =; anything else here is not an operator at all.
        if (selector[index] is '~' or '|' or '^' or '$' or '*')
        {
            index++;
            if (index >= selector.Length || selector[index] != '=')
                return false;
        }
        else if (selector[index] != '=')
        {
            return false;
        }

        index++; // past '='
        SkipWhitespace(selector, ref index);
        if (index >= selector.Length)
            return false;

        if (selector[index] is '"' or '\'')
        {
            if (!SkipString(selector, ref index))
                return false;
            index++;
        }
        else if (!ConsumeIdentifier(selector, ref index))
        {
            // An unquoted value must be an identifier, so `[tabindex=0]` and `[data-x=1]` are
            // errors — measured, not assumed; the digit cannot start one.
            return false;
        }

        SkipWhitespace(selector, ref index);

        // The case-sensitivity flag. Chromium currently takes only `i`; `s` is in Selectors 4 and is
        // accepted here, which can only ever be permissive.
        if (index < selector.Length && selector[index] is 'i' or 'I' or 's' or 'S')
        {
            var flag = index;
            index++;
            SkipWhitespace(selector, ref index);
            if (index >= selector.Length || selector[index] != ']')
            {
                index = flag;
                return false;
            }
        }

        if (index >= selector.Length || selector[index] != ']')
            return false;

        index++;
        return true;
    }

    /// <summary>One <c>:pseudo-class</c> or <c>::pseudo-element</c>, with its argument if it has one.</summary>
    private static bool ConsumePseudo(string selector, ref int index, out bool isPseudoElement)
    {
        isPseudoElement = false;
        index++; // past ':'
        if (index < selector.Length && selector[index] == ':')
        {
            index++;
            isPseudoElement = true;
        }

        var nameStart = index;
        if (!ConsumeIdentifier(selector, ref index))
            return false;
        var name = selector[nameStart..index].ToLowerInvariant();
        isPseudoElement |= LegacyPseudoElements.Contains(name);

        if (index >= selector.Length || selector[index] != '(')
            return true;

        var argumentStart = index + 1;
        var depth = 0;
        for (; index < selector.Length; index++)
        {
            var character = selector[index];
            if (character == '\\')
            {
                index++;
                continue;
            }

            if (character is '"' or '\'')
            {
                if (!SkipString(selector, ref index))
                    return false;
                continue;
            }

            if (character == '(')
            {
                depth++;
            }
            else if (character == ')' && --depth == 0)
            {
                break;
            }
        }

        if (index >= selector.Length || depth != 0)
            return false;

        var argument = selector[argumentStart..index];
        index++; // past ')'

        // An empty argument list is an error for every functional pseudo — `:not()` and
        // `:nth-child()` both throw in a browser.
        if (argument.Trim(Whitespace).Length == 0)
            return false;

        // The four whose argument is itself a selector list get validated as one, so `:not(@bad)`
        // fails. Every other functional pseudo takes something that is not a selector — `2n+1`,
        // `2n of .c`, a language range — and is checked only for being present.
        return name is not ("not" or "is" or "where" or "has") ||
               IsValidSelectorArgument(argument, relative: name == "has");
    }

    /// <summary>The selector-list argument of <c>:not()</c>, <c>:is()</c>, <c>:where()</c> or
    /// <c>:has()</c>, each complex selector validated on its own.</summary>
    private static bool IsValidSelectorArgument(string argument, bool relative)
    {
        var start = 0;
        var parens = 0;
        var brackets = 0;
        for (var index = 0; index < argument.Length; index++)
        {
            var character = argument[index];
            if (character == '\\')
            {
                index++;
                continue;
            }

            if (character is '"' or '\'')
            {
                if (!SkipString(argument, ref index))
                    return false;
                continue;
            }

            switch (character)
            {
                case '(':
                    parens++;
                    break;
                case ')':
                    parens--;
                    break;
                case '[':
                    brackets++;
                    break;
                case ']':
                    brackets--;
                    break;
                case ',' when parens == 0 && brackets == 0:
                    if (!IsValidComplexSelector(argument[start..index], relative))
                        return false;
                    start = index + 1;
                    break;
            }
        }

        return IsValidComplexSelector(argument[start..], relative);
    }

    /// <summary>
    /// One CSS identifier: a start character, then name characters, with escapes allowed anywhere.
    /// </summary>
    /// <remarks>
    /// <c>-</c> is a start character, so <c>-foo</c> and <c>--foo</c> are identifiers and a browser
    /// accepts both as type selectors; a digit is not, so <c>.1a</c> and <c>#1a</c> are errors.
    /// </remarks>
    private static bool ConsumeIdentifier(string selector, ref int index)
    {
        if (index >= selector.Length)
            return false;

        if (selector[index] == '\\')
        {
            if (!ConsumeEscape(selector, ref index))
                return false;
        }
        else if (IsIdentifierStart(selector[index]))
        {
            index++;
        }
        else
        {
            return false;
        }

        while (index < selector.Length)
        {
            if (selector[index] == '\\')
            {
                if (!ConsumeEscape(selector, ref index))
                    return false;
            }
            else if (IsIdentifierPart(selector[index]))
            {
                index++;
            }
            else
            {
                break;
            }
        }

        return true;
    }

    private static bool ConsumeIdentifierOrStar(string selector, ref int index)
    {
        if (index < selector.Length && selector[index] == '*')
        {
            index++;
            return true;
        }

        return ConsumeIdentifier(selector, ref index);
    }

    /// <summary>
    /// A CSS escape: <c>\</c> then either one to six hex digits — optionally followed by a single
    /// whitespace that terminates the escape rather than separating tokens, which is what makes
    /// <c>\31 23</c> one identifier — or any single character that is not a newline.
    /// </summary>
    private static bool ConsumeEscape(string selector, ref int index)
    {
        index++; // past '\'
        if (index >= selector.Length || selector[index] is '\n' or '\r' or '\f')
            return false;

        if (!IsHexDigit(selector[index]))
        {
            index++;
            return true;
        }

        var digits = 0;
        while (digits < 6 && index < selector.Length && IsHexDigit(selector[index]))
        {
            index++;
            digits++;
        }

        if (index < selector.Length && IsWhitespace(selector[index]))
            index++;

        return true;
    }

    /// <summary>Advances <paramref name="index"/> to the closing quote, or reports the string
    /// unterminated. Leaves the index <em>on</em> the quote, as the scanning loops expect.</summary>
    private static bool SkipString(string source, ref int index)
    {
        var quote = source[index];
        for (index++; index < source.Length; index++)
        {
            if (source[index] == '\\')
            {
                index++;
                continue;
            }

            if (source[index] == quote)
                return true;
        }

        return false;
    }

    private static readonly char[] Whitespace = [' ', '\t', '\n', '\r', '\f'];

    private static bool IsWhitespace(char character) => character is ' ' or '\t' or '\n' or '\r' or '\f';

    private static void SkipWhitespace(string source, ref int index)
    {
        while (index < source.Length && IsWhitespace(source[index]))
            index++;
    }

    private static bool IsHexDigit(char character) =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static bool IsIdentifierStart(char character) =>
        char.IsLetter(character) || character is '_' or '-' || character >= 0x80;

    private static bool IsIdentifierPart(char character) =>
        char.IsLetterOrDigit(character) || character is '_' or '-' || character >= 0x80;
}
