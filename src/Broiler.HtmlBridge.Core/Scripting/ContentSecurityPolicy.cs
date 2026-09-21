using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Broiler.Dom.Html;
using Broiler.HtmlBridge.Dom;
using Broiler.HtmlBridge.Internal.Scripting;

namespace Broiler.HtmlBridge.Scripting;

/// <summary>
/// Lightweight Content Security Policy (CSP) model for the bridge script
/// pipeline. The currently honored directives are <c>default-src</c>,
/// <c>script-src</c>, <c>script-src-elem</c>, and <c>script-src-attr</c>
/// for inline-script, external-script, inline event-handler, and
/// <c>eval()</c> gating, plus <c>style-src</c>, <c>style-src-elem</c>, and
/// <c>style-src-attr</c> for gating inline <c>&lt;style&gt;</c> elements and
/// inline <c>style="…"</c> attributes. The currently honored source expressions are
/// <c>'none'</c>, <c>'self'</c>, <c>'unsafe-inline'</c>,
/// <c>'unsafe-eval'</c>, <c>'strict-dynamic'</c>, nonce sources, hash
/// sources, wildcard <c>*</c>, scheme sources such as <c>https:</c>, and
/// absolute origin/path sources.
/// Host wildcards, <c>strict-dynamic</c>'s trust propagation model, and
/// non-script directives are intentionally not yet implemented and therefore
/// remain explicit gaps.
/// </summary>
public sealed class ContentSecurityPolicy
{
    private readonly HashSet<string> _defaultSrcTokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _scriptSrcTokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _scriptSrcElemTokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _scriptSrcAttrTokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _styleSrcTokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _styleSrcElemTokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _styleSrcAttrTokens = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The honored directives by the name a header spells them with, for <see cref="Parse"/> to look a
    /// directive up in. Every entry is one of the sets above, not a copy of it, so a directive parsed
    /// through this table is read back through its field; the fields are what the evaluation below
    /// names, so which directive it means stays a compile-time fact there.
    /// </summary>
    private readonly Dictionary<string, HashSet<string>> _tokensByDirective;

    /// <summary>A policy that allows everything, until <see cref="Parse"/> applies one.</summary>
    public ContentSecurityPolicy()
    {
        // OrdinalIgnoreCase: a directive name is case-insensitive, as `script-src` vs `Script-Src`.
        _tokensByDirective = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["default-src"] = _defaultSrcTokens,
            ["script-src"] = _scriptSrcTokens,
            ["script-src-elem"] = _scriptSrcElemTokens,
            ["script-src-attr"] = _scriptSrcAttrTokens,
            ["style-src"] = _styleSrcTokens,
            ["style-src-elem"] = _styleSrcElemTokens,
            ["style-src-attr"] = _styleSrcAttrTokens,
        };
    }

    /// <summary>
    /// Whether <c>eval()</c> and similar dynamic code execution is allowed.
    /// Defaults to <c>true</c> when no applicable directive is present.
    /// </summary>
    public bool AllowsEval { get; private set; } = true;

    /// <summary>
    /// Whether any honored script directive contains <c>'strict-dynamic'</c>.
    /// </summary>
    public bool StrictDynamic { get; private set; }

    /// <summary>
    /// Parse a CSP header value and apply the honored script directives.
    /// Unknown directives are ignored.
    /// </summary>
    public void Parse(string policy)
    {
        foreach (var directiveTokens in _tokensByDirective.Values)
            directiveTokens.Clear();
        AllowsEval = true;
        StrictDynamic = false;

        if (string.IsNullOrWhiteSpace(policy))
            return;

        var directives = policy.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var directive in directives)
        {
            var tokens = directive.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length == 0)
                continue;

            // A directive this policy does not honor is ignored.
            if (!_tokensByDirective.TryGetValue(tokens[0], out var target))
                continue;

            // The last occurrence of a repeated directive wins, and a source-less one (`script-src;`)
            // stays an empty set, which the fallback chain then reads as "not stated".
            target.Clear();
            for (var i = 1; i < tokens.Length; i++)
                target.Add(tokens[i]);
        }

        AllowsEval = IsEvalAllowed();
        StrictDynamic =
            _defaultSrcTokens.Contains("'strict-dynamic'") ||
            _scriptSrcTokens.Contains("'strict-dynamic'") ||
            _scriptSrcElemTokens.Contains("'strict-dynamic'") ||
            _scriptSrcAttrTokens.Contains("'strict-dynamic'");
    }

    /// <summary>
    /// Returns whether an inline script is allowed under the effective script
    /// element directive.
    /// </summary>
    public bool AllowsInlineScript(string? nonce = null, string? scriptText = null)
    {
        var sources = GetEffectiveScriptElementSources();
        if (sources.Count == 0)
            return true;

        if (IsNoneOnly(sources))
            return false;

        if (!string.IsNullOrEmpty(nonce) && MatchesNonce(sources, nonce))
            return true;

        if (!string.IsNullOrEmpty(scriptText) && MatchesHash(sources, scriptText))
            return true;

        var ignoreUnsafeInline = StrictDynamic && ContainsNonceOrHashSource(sources);
        return !ignoreUnsafeInline && sources.Contains("'unsafe-inline'");
    }

    /// <summary>
    /// Returns whether an inline event handler attribute is allowed under the
    /// effective script attribute directive.
    /// </summary>
    public bool AllowsInlineEventHandler(string? handlerText = null)
    {
        var sources = GetEffectiveScriptAttributeSources();
        if (sources.Count == 0)
            return true;

        if (IsNoneOnly(sources))
            return false;

        if (sources.Contains("'unsafe-inline'"))
            return true;

        if (!string.IsNullOrEmpty(handlerText) &&
            sources.Contains("'unsafe-hashes'") &&
            MatchesHash(sources, handlerText))
            return true;

        return false;
    }

    /// <summary>
    /// Returns whether an inline <c>style="…"</c> attribute is allowed under the
    /// effective style-attribute directive (<c>style-src-attr</c>, falling back
    /// to <c>style-src</c> then <c>default-src</c>). Style attributes cannot carry
    /// a nonce, so only <c>'unsafe-inline'</c> (or the absence of any applicable
    /// directive) permits them.
    /// </summary>
    public bool AllowsInlineStyleAttribute()
    {
        var sources = GetEffectiveStyleAttributeSources();
        if (sources.Count == 0)
            return true;

        if (IsNoneOnly(sources))
            return false;

        return sources.Contains("'unsafe-inline'");
    }

    /// <summary>
    /// Returns whether an inline <c>&lt;style&gt;</c> element is allowed under the
    /// effective style-element directive (<c>style-src-elem</c>, falling back to
    /// <c>style-src</c> then <c>default-src</c>). Honors <c>'unsafe-inline'</c>,
    /// nonce sources, and hash sources.
    /// </summary>
    public bool AllowsInlineStyleElement(string? nonce = null, string? styleText = null)
    {
        var sources = GetEffectiveStyleElementSources();
        if (sources.Count == 0)
            return true;

        if (IsNoneOnly(sources))
            return false;

        if (!string.IsNullOrEmpty(nonce) && MatchesNonce(sources, nonce))
            return true;

        if (!string.IsNullOrEmpty(styleText) && MatchesHash(sources, styleText))
            return true;

        return sources.Contains("'unsafe-inline'");
    }

    /// <summary>
    /// Returns whether this policy could block some inline style — a style
    /// attribute or a plain (nonce-less/hash-less) <c>&lt;style&gt;</c> element.
    /// Lets a caller skip building a DOM purely to enforce styles when the policy
    /// permits them all.
    /// </summary>
    public bool AffectsStyles()
    {
        if (!AllowsInlineStyleAttribute())
            return true;

        var elementSources = GetEffectiveStyleElementSources();
        if (elementSources.Count == 0)
            return false;
        if (IsNoneOnly(elementSources))
            return true;

        return !elementSources.Contains("'unsafe-inline'");
    }

    /// <summary>
    /// Returns whether an external script URL is allowed under the effective
    /// script element directive.
    /// </summary>
    public bool AllowsExternalScript(string scriptUrl, string? pageUrl, string? nonce = null)
    {
        var sources = GetEffectiveScriptElementSources();
        if (sources.Count == 0)
            return true;

        if (IsNoneOnly(sources))
            return false;

        if (!string.IsNullOrEmpty(nonce) && MatchesNonce(sources, nonce))
            return true;

        var ignoreStaticAllowlistSources = StrictDynamic && ContainsNonceOrHashSource(sources);
        if (ignoreStaticAllowlistSources)
            return false;

        var resolved = CspSourceMatching.ResolveUri(scriptUrl, pageUrl);
        if (resolved == null)
            return false;

        return MatchesAnySource(sources, resolved, pageUrl);
    }

    /// <summary>
    /// Returns whether a stylesheet request for <paramref name="styleUrl"/> — a
    /// <c>&lt;link rel="stylesheet"&gt;</c> href, or an <c>@import</c> URL, <c>data:</c> included — is allowed
    /// under the effective <c>style-src-elem</c> → <c>style-src</c> → <c>default-src</c> directive. This is
    /// the style analogue of <see cref="AllowsExternalScript"/>: it applies the same source-token matching
    /// (<c>*</c>, <c>'self'</c>, scheme source, absolute host source) plus the request's nonce, but
    /// not <c>'unsafe-inline'</c> (which does not apply to a fetched URL) nor <c>'strict-dynamic'</c> (a
    /// script-only keyword). A <c>&lt;link&gt;</c> passes its own <c>nonce</c> attribute; an <c>@import</c>
    /// passes none, because "fetch a style resource" gives its request no nonce.
    /// <para>
    /// Per CSP3 §6.7.2.8, <c>*</c> matches HTTP(S) URLs, WebSocket URLs on HTTP(S) pages, or URLs
    /// whose scheme equals the page's own scheme and is not a local scheme. Local schemes (<c>data:</c>,
    /// <c>blob:</c>, <c>filesystem:</c>, <c>javascript:</c>, <c>about:</c>) and cross-scheme non-network
    /// loads (such as <c>file:</c> stylesheets on an <c>http(s)</c> page) require an explicit scheme or
    /// host source and are not admitted by <c>*</c>.
    /// </para>
    /// </summary>
    public bool AllowsExternalStyle(string styleUrl, string? pageUrl, string? nonce = null)
    {
        var sources = GetEffectiveStyleElementSources();
        if (sources.Count == 0)
            return true;

        if (IsNoneOnly(sources))
            return false;

        if (!string.IsNullOrEmpty(nonce) && MatchesNonce(sources, nonce))
            return true;

        var resolved = CspSourceMatching.ResolveUri(styleUrl, pageUrl);
        if (resolved == null)
            return false;

        return MatchesAnySource(sources, resolved, pageUrl);
    }

    /// <summary>
    /// Extract the first CSP policy declared through a
    /// <c>&lt;meta http-equiv=\"Content-Security-Policy\"&gt;</c> tag.
    /// Returns <c>null</c> when no supported policy is present.
    /// </summary>
    public static ContentSecurityPolicy? FromHtml(string html)
    {
        // Discovery (where is the policy in the document) is CspMetaDiscovery's job; this method only
        // composes it with parsing (what the policy allows).
        var content = CspMetaDiscovery.FindPolicyContent(html);
        if (string.IsNullOrWhiteSpace(content))
            return null;

        var policy = new ContentSecurityPolicy();
        policy.Parse(content);
        return policy;
    }

    /// <summary>
    /// Extract a <c>nonce</c> attribute value from a script tag attribute list — the text between
    /// <c>&lt;script</c> and the <c>&gt;</c> that closes it. Returns <c>null</c> when no nonce is present,
    /// and <c>""</c> for an empty or valueless one, which every check here treats as no nonce.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The list is read as the start tag it came from, by the shared <see cref="HtmlTokenizer"/>: the
    /// attribute is matched by its whole name, so <c>data-nonce</c> or a <c>nonce=</c> spelled inside
    /// another attribute's value is not it; quoted, unquoted and upper-case forms are read as a browser
    /// reads them; character references in the value are decoded; and the first of two <c>nonce</c>
    /// attributes wins.
    /// </para>
    /// <para>
    /// A list can arrive cut off inside a quoted value. The CLI's script extraction captures it with
    /// <c>&lt;script(?&lt;attrs&gt;[^&gt;]*)&gt;</c>, which stops at a <c>&gt;</c> even inside quotes, so
    /// <c>&lt;script nonce="abc" data-x="a&gt;b"&gt;</c> arrives as <c> nonce="abc" data-x="a</c>, in which
    /// the tokenizer finds no tag at all. The open quote is then closed and the list read again, so a nonce
    /// spelled before the cut is still the nonce. A nonce whose own value was cut is not: the page's nonce is
    /// longer than what arrived, and matching that prefix against a policy would allow a script a browser
    /// blocks, so it answers <c>null</c>.
    /// </para>
    /// <para>
    /// Where the tokenizer otherwise departs from the HTML Standard, so does this — notably, a character
    /// reference is decoded only when it ends in <c>;</c> and names an HTML 4 entity, and attributes are
    /// separated by any Unicode whitespace rather than ASCII whitespace alone.
    /// </para>
    /// </remarks>
    public static string? ExtractNonceFromAttributes(string attributes)
    {
        if (string.IsNullOrWhiteSpace(attributes))
            return null;

        if (TryReadNonce(attributes + ">", out var nonce))
            return nonce;

        // Cut off inside a quoted value. Closing the other quote leaves that value just as open, so at
        // most one of the two reads makes a tag. A character added before the closing quote lands in the
        // value that was cut, so the nonce changes with it only when the nonce is that value.
        foreach (var quote in "\"'")
        {
            if (!TryReadNonce(attributes + quote + ">", out nonce))
                continue;

            TryReadNonce(attributes + "x" + quote + ">", out var extended);
            return nonce == extended ? nonce : null;
        }

        return null;
    }

    /// <summary>
    /// Reads <c>&lt;script </c> followed by <paramref name="tagText"/> as a start tag. Answers whether it
    /// made one at all, with its <c>nonce</c> when it has one.
    /// </summary>
    private static bool TryReadNonce(string tagText, out string? nonce)
    {
        // The tokenizer is lazy and the start tag is its first token, so nothing after it is read. When
        // the tag is unfinished at the end of the text, the first token is the end of the input instead.
        var tag = new HtmlTokenizer().Tokenize("<script " + tagText).First();
        nonce = tag.Type == TokenType.StartTag && tag.Attributes.TryGetValue("nonce", out var value) ? value : null;
        return tag.Type == TokenType.StartTag;
    }

    private bool IsEvalAllowed()
    {
        var sources = _scriptSrcTokens.Count > 0 ? _scriptSrcTokens : _defaultSrcTokens;
        if (sources.Count == 0)
            return true;

        if (IsNoneOnly(sources))
            return false;

        return sources.Contains("'unsafe-eval'");
    }

    private HashSet<string> GetEffectiveScriptElementSources()
        => EffectiveSources(_scriptSrcElemTokens, _scriptSrcTokens);

    private HashSet<string> GetEffectiveScriptAttributeSources()
        => EffectiveSources(_scriptSrcAttrTokens, _scriptSrcTokens);

    private HashSet<string> GetEffectiveStyleElementSources()
        => EffectiveSources(_styleSrcElemTokens, _styleSrcTokens);

    private HashSet<string> GetEffectiveStyleAttributeSources()
        => EffectiveSources(_styleSrcAttrTokens, _styleSrcTokens);

    // The fallback chain every one of those four getters walks: the directive's own tokens, then the
    // group directive it falls back to (script-src or style-src), then default-src. A directive that
    // was never stated is an empty set rather than a missing one, which is why each step tests
    // Count > 0. The set is handed back by reference, as Parse clears these in place and every caller
    // only reads it.
    private HashSet<string> EffectiveSources(HashSet<string> specific, HashSet<string> group)
        => specific.Count > 0 ? specific : group.Count > 0 ? group : _defaultSrcTokens;

    private static bool IsNoneOnly(HashSet<string> sources)
        => sources.Count == 1 && sources.Contains("'none'");

    private static bool ContainsNonceOrHashSource(HashSet<string> sources)
    {
        foreach (var source in sources)
        {
            if (source.StartsWith("'nonce-", StringComparison.OrdinalIgnoreCase) ||
                source.StartsWith("'sha256-", StringComparison.OrdinalIgnoreCase) ||
                source.StartsWith("'sha384-", StringComparison.OrdinalIgnoreCase) ||
                source.StartsWith("'sha512-", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    // Whether any source token in the set admits a fetched URL: the wildcard, 'self', a scheme source
    // such as https:, or an absolute host source, tried in that order. The keyword sources that speak
    // about inline content — 'unsafe-inline', nonces, hashes, 'strict-dynamic' — are not tested here,
    // because a fetched URL is not inline content; each caller applies the ones that apply to it
    // before asking. Shared by AllowsExternalScript and AllowsExternalStyle.
    private static bool MatchesAnySource(HashSet<string> sources, Uri resolved, string? pageUrl)
    {
        foreach (var source in sources)
        {
            if (string.Equals(source, "*", StringComparison.Ordinal) &&
                CspSourceMatching.MatchesWildcard(resolved, pageUrl))
                return true;

            if (string.Equals(source, "'self'", StringComparison.OrdinalIgnoreCase) &&
                CspSourceMatching.IsSameOrigin(resolved, pageUrl))
                return true;

            if (CspSourceMatching.IsSchemeSource(source) &&
                string.Equals(resolved.Scheme, source[..^1], StringComparison.OrdinalIgnoreCase))
                return true;

            if (CspSourceMatching.MatchesAbsoluteSource(source, resolved))
                return true;
        }

        return false;
    }

    private static bool MatchesNonce(HashSet<string> sources, string nonce)
    {
        foreach (var source in sources)
        {
            if (!source.StartsWith("'nonce-", StringComparison.OrdinalIgnoreCase) || source.Length < 9)
                continue;

            var declared = source[7..^1];
            if (string.Equals(declared, nonce, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool MatchesHash(HashSet<string> sources, string scriptText)
    {
        foreach (var source in sources)
        {
            if (!source.StartsWith("'sha", StringComparison.OrdinalIgnoreCase) || source.Length < 10)
                continue;

            var separatorIndex = source.IndexOf('-', 1);
            if (separatorIndex < 0 || !source.EndsWith('\''))
                continue;

            var algorithm = source[1..separatorIndex];
            var declared = source[(separatorIndex + 1)..^1];
            var actual = algorithm.ToLowerInvariant() switch
            {
                "sha256" => ComputeBase64Hash(scriptText, SHA256.HashData),
                "sha384" => ComputeBase64Hash(scriptText, SHA384.HashData),
                "sha512" => ComputeBase64Hash(scriptText, SHA512.HashData),
                _ => null
            };

            if (!string.IsNullOrEmpty(actual) &&
                string.Equals(actual, declared, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static string? ComputeBase64Hash(string value, Func<byte[], byte[]> hasher)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var hash = hasher(bytes);
        return Convert.ToBase64String(hash);
    }

}
