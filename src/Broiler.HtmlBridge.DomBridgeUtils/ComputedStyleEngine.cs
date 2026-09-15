namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    /// <summary>
    /// The <c>::part()</c> rules of a stylesheet, returned as CSS text. A brace-depth scan rather
    /// than a parse: it keeps each qualifying rule's own text verbatim, so the cascade sees exactly
    /// what the author wrote. Rules nested in an at-rule (<c>@media</c> …) are not lifted out — a
    /// <c>::part()</c> inside one still will not cross into a shadow tree.
    /// </summary>
    private static string ExtractPartRules(string css)
    {
        if (string.IsNullOrEmpty(css) || css.IndexOf("::part(", StringComparison.OrdinalIgnoreCase) < 0)
            return string.Empty;

        var kept = new System.Text.StringBuilder();
        var depth = 0;
        var ruleStart = 0;
        var preludeEnd = -1;

        for (var index = 0; index < css.Length; index++)
        {
            var character = css[index];
            if (character == '{')
            {
                if (depth == 0)
                    preludeEnd = index;
                depth++;
            }
            else if (character == '}')
            {
                depth--;
                if (depth != 0)
                    continue;

                if (preludeEnd > ruleStart &&
                    css.AsSpan(ruleStart, preludeEnd - ruleStart)
                        .IndexOf("::part(", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    kept.Append(css, ruleStart, index - ruleStart + 1).Append('\n');
                }

                ruleStart = index + 1;
                preludeEnd = -1;
            }
        }

        return kept.ToString();
    }
}
