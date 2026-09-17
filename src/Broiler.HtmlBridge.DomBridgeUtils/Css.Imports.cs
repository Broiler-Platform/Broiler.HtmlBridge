using System.Text;

namespace Broiler.HtmlBridge;

/// <summary>
/// The leading <c>@import</c> rules of a style sheet, read for the render projection that inlines them
/// (<c>DomBridge.InlineStyleSheetImports</c>): where the run of imports ends, and each import's URL,
/// <c>layer</c>, <c>supports()</c> and media list. Pure text parsing; what is fetched, and whether the
/// Content Security Policy allows it, is decided by the caller.
/// </summary>
public static partial class DomBridgeUtils
{
    /// <summary>
    /// Whether <paramref name="css"/> begins with at least one <c>@import</c> that
    /// <see cref="ScanLeadingImports"/> would collect, so a sheet with none skips the expansion. Only a
    /// sheet with no <c>@import</c> text anywhere is answered without allocating; any other sheet runs
    /// the whole scan, prelude parsing and lists included.
    /// </summary>
    internal static bool HasLeadingImport(string css)
        => css.Contains("@import", StringComparison.OrdinalIgnoreCase) &&
           ScanLeadingImports(css).Imports.Count > 0;

    /// <summary>The layer an <c>@import</c> puts its sheet in (CSS Cascade 5 §2).</summary>
    internal enum ImportLayer
    {
        /// <summary>No <c>layer</c> part: the sheet's rules are unlayered.</summary>
        None,

        /// <summary>The bare <c>layer</c> keyword: a new anonymous layer, distinct from every other.</summary>
        Anonymous,

        /// <summary><c>layer(&lt;layer-name&gt;)</c>, the name in <see cref="ImportPrelude.LayerName"/>.</summary>
        Named,
    }

    /// <summary>
    /// An <c>@import</c> prelude split along the CSS Cascade 5 §2 grammar,
    /// <c>@import [ &lt;url&gt; | &lt;string&gt; ] [ layer | layer(&lt;layer-name&gt;) ]?
    /// [ supports( [ &lt;supports-condition&gt; | &lt;declaration&gt; ] ) ]? &lt;media-query-list&gt;?</c>.
    /// </summary>
    /// <param name="Href">
    /// The URL with its CSS escapes decoded, or empty when the prelude has none, or only a bad one (a bad URL or
    /// string, which makes the import invalid).
    /// </param>
    /// <param name="Layer">Which of the two <c>layer</c> forms was given, if either.</param>
    /// <param name="LayerName">
    /// The dotted <c>&lt;layer-name&gt;</c> of a <see cref="ImportLayer.Named"/> layer, as written, without comments.
    /// </param>
    /// <param name="Supports">
    /// The raw text inside <c>supports( … )</c>, trimmed and with any comments still in it, or <see langword="null"/>
    /// without one.
    /// </param>
    /// <param name="Media">Everything after the parts above: the media query list, trimmed, and empty when there is none.</param>
    internal readonly record struct ImportPrelude(
        string Href, ImportLayer Layer, string? LayerName, string? Supports, string Media);

    /// <summary>
    /// Scans the leading portion of a stylesheet, where <c>@import</c> rules are valid, collecting each
    /// one's parsed prelude and the offset at which the first other content begins. Whitespace, comments
    /// and the <c>&lt;!--</c>/<c>--&gt;</c> tokens a stylesheet ignores at its top level are skipped, and
    /// so is every at-rule statement that cannot end the run: <c>@charset</c>, and any unknown one such as
    /// <c>@foo;</c>, which is invalid and so leaves the imports after it valid (Chromium changes nothing
    /// for an invalid rule; Chrome 152 probe L13). The run ends at a style rule, at <c>@namespace</c>, and
    /// at any at-rule with a block, an <c>@import</c> followed by one included (invalid, and left in place
    /// rather than read on into the next rule). An <c>@import</c> after the run is left in place for the
    /// renderer, which ignores it, as a browser does.
    /// <para>
    /// <c>@layer</c> statement rules — an <c>@layer</c> with no block, such as <c>@layer reset, base;</c>
    /// — may precede the imports (Cascade 5 §2; Chrome 152 probe L11). They are returned verbatim, in
    /// order, in <c>LayerStatements</c>, so the caller that replaces the scanned prefix keeps them ahead
    /// of what it inlines rather than dropping the layer order they declare. Once an <c>@import</c> has
    /// been seen, an <c>@layer</c> statement ends the run instead: Cascade 5 §6.4.4.2 makes every
    /// <c>@import</c> after it invalid, and Chrome neither applies nor fetches one (probe L12b), so it
    /// must not be inlined; the statement and all that follows stay past <c>EndOffset</c>. Only a valid
    /// import counts: one with no URL is invalid and changes nothing, as in Chromium. An <c>@layer</c>
    /// block ends the run wherever it is. Whether a statement's layer names are valid is left to the
    /// engine that parses the kept text.
    /// </para>
    /// <para>
    /// Not handled, and each costs at most that one import: a sheet that ends inside an import's URL or
    /// string, which css-syntax-3 closes at the end of input and this reads as having no URL; one that
    /// ends inside its parentheses or a comment, which then stays in the media text and makes it false;
    /// and an at-keyword spelled with an escape, where the run ends.
    /// </para>
    /// </summary>
    internal static (int EndOffset, List<string> LayerStatements, List<ImportPrelude> Imports) ScanLeadingImports(string css)
    {
        var layerStatements = new List<string>();
        var imports = new List<ImportPrelude>();
        var sawValidImport = false;
        var i = 0;
        var n = css.Length;
        var consumedEnd = 0;

        while (i < n)
        {
            // Skip whitespace.
            while (i < n && char.IsWhiteSpace(css[i]))
                i++;
            if (i == n)
                break;

            // Skip /* comments */.
            if (i + 1 < n && css[i] == '/' && css[i + 1] == '*')
            {
                i = SkipComment(css, i);
                continue;
            }

            // <!-- and --> mean nothing here (css-syntax-3 "consume a list of rules" with the top-level
            // flag). They are dropped with the prefix, because the renderer's parser does not skip them.
            if (string.CompareOrdinal(css, i, "<!--", 0, 4) == 0 || string.CompareOrdinal(css, i, "-->", 0, 3) == 0)
            {
                i += css[i] == '<' ? 4 : 3;
                consumedEnd = i;
                continue;
            }

            if (StartsWithAtKeyword(css, i, "@import"))
            {
                var stmtEnd = ScanStatement(css, i, out var opensBlock);
                if (opensBlock)
                    break;

                var prelude = css[(i + "@import".Length)..stmtEnd].Trim().TrimEnd(';').Trim();
                var import = ParseImportPrelude(prelude);
                imports.Add(import);
                sawValidImport |= import.Href.Length > 0;
                i = stmtEnd;
                consumedEnd = i;
                continue;
            }

            if (StartsWithAtKeyword(css, i, "@layer"))
            {
                var stmtEnd = ScanStatement(css, i, out var opensBlock);
                if (opensBlock || sawValidImport)
                    break;

                layerStatements.Add(css[i..stmtEnd]);
                i = stmtEnd;
                consumedEnd = i;
                continue;
            }

            // @charset, or an at-rule statement nothing recognizes. @namespace is valid, and ends the run.
            if (i + 1 < n && css[i] == '@' && (IsNameStartChar(css[i + 1]) || css[i + 1] == '-') &&
                !StartsWithAtKeyword(css, i, "@namespace"))
            {
                var stmtEnd = ScanStatement(css, i, out var opensBlock);
                if (opensBlock)
                    break;

                i = stmtEnd;
                consumedEnd = i;
                continue;
            }

            break;
        }

        return (consumedEnd, layerStatements, imports);
    }

    /// <summary>Whether <paramref name="css"/> at <paramref name="pos"/> begins the at-rule
    /// <paramref name="keyword"/> (case-insensitive) followed by a non-identifier delimiter,
    /// so <c>@import</c> is not matched inside <c>@imports-like</c>. A comment is a delimiter too:
    /// <c>@import/**/url(a.css)</c> is an import.</summary>
    private static bool StartsWithAtKeyword(string css, int pos, string keyword)
    {
        if (pos + keyword.Length > css.Length)
            return false;
        if (string.Compare(css, pos, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0)
            return false;
        var after = pos + keyword.Length;
        if (after == css.Length)
            return true;
        var next = css[after];
        return char.IsWhiteSpace(next) || next == '"' || next == '\'' || next == '(' || next == ';' ||
               (next == '/' && after + 1 < css.Length && css[after + 1] == '*');
    }

    /// <summary>
    /// Returns the index just past the <c>;</c> that ends the statement starting at
    /// <paramref name="start"/>, or end-of-input for an unterminated one, and reports whether a
    /// <c>{</c> outside any parentheses came first — a rule with a block rather than a statement, for
    /// which the returned index means nothing (the <c>;</c> it stopped at may be inside the block).
    /// A <c>;</c> inside a string, a comment, parentheses or an escape does not end the statement, and
    /// a <c>)</c> inside a quoted string does not close its parentheses, so
    /// <c>url("a);b.css")</c> stays one statement. An unquoted <c>url(</c> runs to its first unescaped
    /// <c>)</c> whatever it contains, as the CSS tokenizer reads it, so a stray quote inside one cannot
    /// swallow the rest of the sheet.
    /// </summary>
    private static int ScanStatement(string css, int start, out bool opensBlock)
    {
        opensBlock = false;
        var i = start;
        var n = css.Length;
        var depth = 0;
        while (i < n)
        {
            var c = css[i];
            if (c == '"' || c == '\'')
            {
                i = SkipString(css, i);
            }
            else if (c == '/' && i + 1 < n && css[i + 1] == '*')
            {
                i = SkipComment(css, i);
            }
            else if (c == '\\')
            {
                i = System.Math.Min(n, i + 2);
            }
            else if ((c == 'u' || c == 'U') && IsUnquotedUrlStart(css, i, out var bodyStart))
            {
                i = SkipUnquotedUrlBody(css, bodyStart);
            }
            else
            {
                if (c == '(')
                {
                    depth++;
                }
                else if (c == ')')
                {
                    if (depth > 0)
                        depth--;
                }
                else if (depth == 0 && c == ';')
                {
                    return i + 1;
                }
                else if (depth == 0 && c == '{' && !opensBlock)
                {
                    opensBlock = true;
                }

                i++;
            }
        }
        return n;
    }

    /// <summary>
    /// Splits an <c>@import</c> prelude (after the keyword, without the trailing <c>;</c>) into its
    /// parts, reading them only in the grammar's order: the URL (<c>url(…)</c> or a string), then an
    /// optional <c>layer</c> or <c>layer(&lt;layer-name&gt;)</c>, then an optional <c>supports(…)</c>,
    /// and everything left is the media query list. Keywords and function names ignore case, and
    /// whitespace and comments between parts are skipped.
    /// <para>
    /// A part that is out of order, or a <c>layer(…)</c> whose contents are not a layer name, is not
    /// consumed and stays in the media text, as Chromium's parser leaves it: <c>@media</c> then
    /// evaluates <c>layer(1bad)</c> or <c>supports(x) layer(base)</c>'s trailing <c>layer(base)</c> as a
    /// false query, and the sheet does not apply, which is Chrome 152's answer too (probes L6, L7).
    /// Everything after the URL used to be the media text, so every <c>layer</c> and <c>supports()</c>
    /// became a query that never matched.
    /// </para>
    /// <para>
    /// A quoted URL — <c>url("x(1).css")</c>, <c>url('…')</c> or a bare string — ends at its matching
    /// quote, and an unquoted <c>url(…)</c> at its first unescaped <c>)</c>; backslash escapes are
    /// honoured and decoded in both (css-syntax-3 §4.3.7). The old split ended every <c>url(</c> at its
    /// first <c>)</c>, so <c>url("x(1).css")</c> fetched <c>x(1</c>.
    /// </para>
    /// </summary>
    private static ImportPrelude ParseImportPrelude(string prelude)
    {
        var i = SkipWhitespaceAndComments(prelude, 0);
        if (!TryReadImportUrl(prelude, ref i, out var href))
            return new ImportPrelude(string.Empty, ImportLayer.None, null, null, string.Empty);

        i = SkipWhitespaceAndComments(prelude, i);
        var layer = ImportLayer.None;
        string? layerName = null;
        if (StartsWithIdent(prelude, i, "layer"))
        {
            var afterKeyword = i + "layer".Length;
            if (afterKeyword < prelude.Length && prelude[afterKeyword] == '(')
            {
                if (TryFindClosingParenthesis(prelude, afterKeyword, out var close) &&
                    TryReadLayerName(prelude, afterKeyword + 1, close, out var name))
                {
                    (layer, layerName) = (ImportLayer.Named, name);
                    i = SkipWhitespaceAndComments(prelude, close + 1);
                }
            }
            else
            {
                layer = ImportLayer.Anonymous;
                i = SkipWhitespaceAndComments(prelude, afterKeyword);
            }
        }

        string? supports = null;
        var afterSupports = i + "supports".Length;
        if (StartsWithIdent(prelude, i, "supports") &&
            afterSupports < prelude.Length && prelude[afterSupports] == '(' &&
            TryFindClosingParenthesis(prelude, afterSupports, out var supportsClose))
        {
            supports = prelude[(afterSupports + 1)..supportsClose].Trim();
            i = SkipWhitespaceAndComments(prelude, supportsClose + 1);
        }

        return new ImportPrelude(href, layer, layerName, supports, prelude[i..].Trim());
    }

    /// <summary>Reads the <c>url(…)</c> or string at <paramref name="i"/>, leaving <paramref name="i"/>
    /// just past it. Fails, leaving it where it was, when neither is there or it is unterminated.</summary>
    private static bool TryReadImportUrl(string text, ref int i, out string href)
    {
        href = string.Empty;
        if (i < text.Length && (text[i] == '"' || text[i] == '\''))
            return TryReadCssString(text, ref i, out href);

        if (string.Compare(text, i, "url(", 0, 4, StringComparison.OrdinalIgnoreCase) != 0)
            return false;

        var j = i + 4;
        while (j < text.Length && IsCssWhitespace(text[j]))
            j++;

        if (j < text.Length && (text[j] == '"' || text[j] == '\''))
        {
            if (!TryReadCssString(text, ref j, out var quoted))
                return false;
            while (j < text.Length && IsCssWhitespace(text[j]))
                j++;
            if (j >= text.Length || text[j] != ')')
                return false;

            (href, i) = (quoted, j + 1);
            return true;
        }

        // css-syntax-3 §4.3.6 "consume a url token": whitespace may only pad the end, and a quote, an
        // opening parenthesis, a non-printable character or an invalid escape makes a bad URL, which
        // leaves the import without one. A browser requests nothing for it, so neither does this.
        var unquoted = new StringBuilder();
        while (j < text.Length && text[j] != ')')
        {
            var c = text[j];
            if (IsCssWhitespace(c))
            {
                while (j < text.Length && IsCssWhitespace(text[j]))
                    j++;
                if (j < text.Length && text[j] != ')')
                    return false;
            }
            else if (c is '"' or '\'' or '(' ||
                     c is (>= '\0' and <= '\b') or '\v' or (>= '\u000E' and <= '\u001F') or '\u007F')
            {
                return false;
            }
            else if (c == '\\')
            {
                if (!IsValidEscape(text, j))
                    return false;
                AppendCssEscape(text, ref j, unquoted);
            }
            else
            {
                unquoted.Append(text[j++]);
            }
        }

        if (j >= text.Length)
            return false;

        (href, i) = (unquoted.ToString(), j + 1);
        return true;
    }

    /// <summary>Reads the quoted string at <paramref name="i"/> with its escapes decoded, leaving
    /// <paramref name="i"/> just past the closing quote. Fails on an unterminated string, and on a bad
    /// one: an unescaped newline ends a string (css-syntax-3 §4.3.5), and the import with it.</summary>
    private static bool TryReadCssString(string text, ref int i, out string value)
    {
        var quote = text[i];
        var decoded = new StringBuilder();
        var j = i + 1;
        while (j < text.Length && text[j] is not ('\n' or '\r' or '\f'))
        {
            var c = text[j];
            if (c == quote)
            {
                (value, i) = (decoded.ToString(), j + 1);
                return true;
            }

            if (c == '\\')
                AppendCssEscape(text, ref j, decoded);
            else
                decoded.Append(text[j++]);
        }

        value = string.Empty;
        return false;
    }

    /// <summary>
    /// Consumes the escape whose backslash is at <paramref name="i"/> and appends what it stands for
    /// (css-syntax-3 §4.3.7), or only consumes it when <paramref name="into"/> is <see langword="null"/>:
    /// up to six hex digits, and one whitespace after them, are a code point (U+FFFD for zero, a
    /// surrogate, or anything above U+10FFFF); an escaped newline is a string's line continuation and
    /// stands for nothing; any other character stands for itself.
    /// </summary>
    private static void AppendCssEscape(string text, ref int i, StringBuilder? into)
    {
        i++;
        if (i >= text.Length)
            return;

        if (char.IsAsciiHexDigit(text[i]))
        {
            var codePoint = 0;
            var digitsEnd = System.Math.Min(text.Length, i + 6);
            while (i < digitsEnd && char.IsAsciiHexDigit(text[i]))
            {
                var digit = text[i++];
                codePoint = (codePoint * 16) + (digit <= '9' ? digit - '0' : (digit | 0x20) - 'a' + 10);
            }

            if (i < text.Length && IsCssWhitespace(text[i]))
                i += text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;

            into?.Append(codePoint == 0 || codePoint > 0x10FFFF || codePoint is >= 0xD800 and <= 0xDFFF
                ? "�"
                : char.ConvertFromUtf32(codePoint));
            return;
        }

        if (text[i] is '\n' or '\r' or '\f')
        {
            i += text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
            return;
        }

        into?.Append(text[i]);
        i++;
    }

    /// <summary>Whether <paramref name="text"/> at <paramref name="i"/> is the identifier
    /// <paramref name="word"/> (case-insensitive) — not the start of a longer one, so <c>layered</c> is
    /// not <c>layer</c>. A <c>(</c> may follow, making it a function name.</summary>
    private static bool StartsWithIdent(string text, int i, string word)
    {
        if (i + word.Length > text.Length ||
            string.Compare(text, i, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) != 0)
            return false;

        var next = i + word.Length;
        return next == text.Length || !(IsNameChar(text[next]) || text[next] == '\\');
    }

    /// <summary>
    /// Whether <paramref name="text"/> from <paramref name="start"/> to <paramref name="end"/> (the inside
    /// of <c>layer( … )</c>) is a <c>&lt;layer-name&gt;</c>: <c>&lt;ident&gt;</c> segments joined by
    /// <c>.</c> with no whitespace between them (Cascade 5 §6.4.2), which whitespace and comments may pad.
    /// A comment between segments separates tokens without being whitespace, so <c>a/**/.b</c> is the
    /// name <c>a.b</c>, as Chromium's token-based parser reads it, while <c>a/**/b</c> is two idents and
    /// no name. <paramref name="name"/> is the name as written, without its comments. Chromium's import
    /// parser does not reject the CSS-wide keywords here, and neither does this.
    /// </summary>
    private static bool TryReadLayerName(string text, int start, int end, out string name)
    {
        name = string.Empty;
        var written = new StringBuilder();
        var i = SkipWhitespaceAndComments(text, start);
        while (true)
        {
            var segmentStart = i;
            if (!TryConsumeIdent(text, ref i))
                return false;

            written.Append(text, segmentStart, i - segmentStart);
            i = SkipComments(text, i);
            if (i >= end || text[i] != '.')
                break;

            written.Append('.');
            i = SkipComments(text, i + 1);
        }

        if (SkipWhitespaceAndComments(text, i) != end)
            return false;

        name = written.ToString();
        return true;
    }

    /// <summary>Consumes the <c>&lt;ident-token&gt;</c> at <paramref name="i"/> (css-syntax-3 §4.3.9
    /// "would start an ident sequence", then name code points and escapes, a hex escape with the
    /// whitespace that ends it).</summary>
    private static bool TryConsumeIdent(string text, ref int i)
    {
        var j = i;
        if (j < text.Length && text[j] == '-')
        {
            j++;
            if (j < text.Length && text[j] == '-')
                j++;
            else if (!StartsName(text, j))
                return false;
        }
        else if (!StartsName(text, j))
        {
            return false;
        }

        while (j < text.Length)
        {
            if (IsValidEscape(text, j))
                AppendCssEscape(text, ref j, into: null);
            else if (IsNameChar(text[j]))
                j++;
            else
                break;
        }

        i = j;
        return true;

        static bool StartsName(string s, int k) =>
            k < s.Length && (IsNameStartChar(s[k]) || IsValidEscape(s, k));
    }

    private static bool IsValidEscape(string text, int i) =>
        text[i] == '\\' && i + 1 < text.Length && text[i + 1] is not ('\n' or '\r' or '\f');

    private static bool IsNameStartChar(char c) => char.IsAsciiLetter(c) || c == '_' || c >= 0x80;

    private static bool IsNameChar(char c) => IsNameStartChar(c) || char.IsAsciiDigit(c) || c == '-';

    private static bool IsCssWhitespace(char c) => c is ' ' or '\t' or '\n' or '\r' or '\f';

    /// <summary>Finds the <c>)</c> matching the <c>(</c> at <paramref name="open"/>, stepping over
    /// strings, comments and escapes. Fails when the parentheses are not closed.</summary>
    private static bool TryFindClosingParenthesis(string text, int open, out int close)
    {
        var depth = 0;
        var i = open;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '"' || c == '\'')
            {
                i = SkipString(text, i);
                continue;
            }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i = SkipComment(text, i);
                continue;
            }
            if (c == '\\')
            {
                i += 2;
                continue;
            }
            if (c == '(')
                depth++;
            else if (c == ')' && --depth == 0)
            {
                close = i;
                return true;
            }
            i++;
        }

        close = -1;
        return false;
    }

    /// <summary>Whether an unquoted <c>url(</c> starts at <paramref name="i"/> — the function name,
    /// not the tail of a longer identifier, followed by optional whitespace and anything but a quote —
    /// with <paramref name="bodyStart"/> the index after its <c>(</c>.</summary>
    private static bool IsUnquotedUrlStart(string text, int i, out int bodyStart)
    {
        bodyStart = i + 4;
        if ((i > 0 && (IsNameChar(text[i - 1]) || text[i - 1] == '\\')) ||
            string.Compare(text, i, "url(", 0, 4, StringComparison.OrdinalIgnoreCase) != 0)
            return false;

        var j = bodyStart;
        while (j < text.Length && IsCssWhitespace(text[j]))
            j++;
        return j >= text.Length || (text[j] != '"' && text[j] != '\'');
    }

    /// <summary>Returns the index just past the first unescaped <c>)</c> at or after
    /// <paramref name="i"/>, or end-of-input.</summary>
    private static int SkipUnquotedUrlBody(string text, int i)
    {
        while (i < text.Length && text[i] != ')')
            i += text[i] == '\\' ? 2 : 1;
        return System.Math.Min(text.Length, i + 1);
    }

    /// <summary>Returns the index just past the string whose opening quote is at
    /// <paramref name="i"/>, stepping over escapes; end-of-input when it is unterminated. A bad string
    /// ends at its unescaped newline (css-syntax-3 §4.3.5), and the index of that newline is returned,
    /// so an unmatched quote cannot swallow the rules after it.</summary>
    private static int SkipString(string text, int i)
    {
        var quote = text[i++];
        while (i < text.Length && text[i] != quote)
        {
            if (text[i] is '\n' or '\r' or '\f')
                return i;
            i += text[i] == '\\' ? 2 : 1;
        }
        return System.Math.Min(text.Length, i + 1);
    }

    /// <summary>Returns the index just past the comment whose <c>/*</c> is at <paramref name="i"/>;
    /// end-of-input when it is unterminated.</summary>
    private static int SkipComment(string text, int i)
    {
        i += 2;
        while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/'))
            i++;
        return System.Math.Min(text.Length, i + 2);
    }

    private static int SkipComments(string text, int i)
    {
        while (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '*')
            i = SkipComment(text, i);
        return i;
    }

    private static int SkipWhitespaceAndComments(string text, int i)
    {
        while (i < text.Length)
        {
            if (IsCssWhitespace(text[i]))
                i++;
            else if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
                i = SkipComment(text, i);
            else
                break;
        }
        return i;
    }
}
