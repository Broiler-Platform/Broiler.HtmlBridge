using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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
        _defaultSrcTokens.Clear();
        _scriptSrcTokens.Clear();
        _scriptSrcElemTokens.Clear();
        _scriptSrcAttrTokens.Clear();
        _styleSrcTokens.Clear();
        _styleSrcElemTokens.Clear();
        _styleSrcAttrTokens.Clear();
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

            HashSet<string>? target = null;
            if (string.Equals(tokens[0], "default-src", StringComparison.OrdinalIgnoreCase))
                target = _defaultSrcTokens;
            else if (string.Equals(tokens[0], "script-src", StringComparison.OrdinalIgnoreCase))
                target = _scriptSrcTokens;
            else if (string.Equals(tokens[0], "script-src-elem", StringComparison.OrdinalIgnoreCase))
                target = _scriptSrcElemTokens;
            else if (string.Equals(tokens[0], "script-src-attr", StringComparison.OrdinalIgnoreCase))
                target = _scriptSrcAttrTokens;
            else if (string.Equals(tokens[0], "style-src", StringComparison.OrdinalIgnoreCase))
                target = _styleSrcTokens;
            else if (string.Equals(tokens[0], "style-src-elem", StringComparison.OrdinalIgnoreCase))
                target = _styleSrcElemTokens;
            else if (string.Equals(tokens[0], "style-src-attr", StringComparison.OrdinalIgnoreCase))
                target = _styleSrcAttrTokens;

            if (target == null)
                continue;

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

        foreach (var source in sources)
        {
            if (string.Equals(source, "*", StringComparison.Ordinal))
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

    /// <summary>
    /// Returns whether an external stylesheet URL (a <c>&lt;link rel="stylesheet"&gt;</c> href) is allowed
    /// under the effective <c>style-src-elem</c> → <c>style-src</c> → <c>default-src</c> directive. This is
    /// the style analogue of <see cref="AllowsExternalScript"/>: it applies the same source-token matching
    /// (<c>*</c>, <c>'self'</c>, scheme source, absolute host source) plus a <c>&lt;link&gt;</c> nonce, but
    /// not <c>'unsafe-inline'</c> (which does not apply to a fetched URL) nor <c>'strict-dynamic'</c> (a
    /// script-only keyword).
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

        foreach (var source in sources)
        {
            if (string.Equals(source, "*", StringComparison.Ordinal))
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

    /// <summary>
    /// Extract the first CSP policy declared through a
    /// <c>&lt;meta http-equiv=\"Content-Security-Policy\"&gt;</c> tag.
    /// Returns <c>null</c> when no supported policy is present.
    /// </summary>
    public static ContentSecurityPolicy? FromHtml(string html)
    {
        // Discovery (where is the policy in the document) is CspMetaDiscovery's job; this method only
        // composes it with parsing (what the policy allows). Phase 7 item 1.
        var content = CspMetaDiscovery.FindPolicyContent(html);
        if (string.IsNullOrWhiteSpace(content))
            return null;

        var policy = new ContentSecurityPolicy();
        policy.Parse(content);
        return policy;
    }

    /// <summary>
    /// Extract a <c>nonce</c> attribute value from a script tag attribute list.
    /// Returns <c>null</c> when no nonce is present.
    /// </summary>
    public static string? ExtractNonceFromAttributes(string attributes) =>
        HtmlAttributeReader.ExtractAttributeValue(attributes, "nonce");

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
    {
        if (_scriptSrcElemTokens.Count > 0)
            return _scriptSrcElemTokens;
        if (_scriptSrcTokens.Count > 0)
            return _scriptSrcTokens;
        return _defaultSrcTokens;
    }

    private HashSet<string> GetEffectiveScriptAttributeSources()
    {
        if (_scriptSrcAttrTokens.Count > 0)
            return _scriptSrcAttrTokens;
        if (_scriptSrcTokens.Count > 0)
            return _scriptSrcTokens;
        return _defaultSrcTokens;
    }

    private HashSet<string> GetEffectiveStyleElementSources()
    {
        if (_styleSrcElemTokens.Count > 0)
            return _styleSrcElemTokens;
        if (_styleSrcTokens.Count > 0)
            return _styleSrcTokens;
        return _defaultSrcTokens;
    }

    private HashSet<string> GetEffectiveStyleAttributeSources()
    {
        if (_styleSrcAttrTokens.Count > 0)
            return _styleSrcAttrTokens;
        if (_styleSrcTokens.Count > 0)
            return _styleSrcTokens;
        return _defaultSrcTokens;
    }

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
