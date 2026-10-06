using System;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The URL of a document in hand, behind its Location's <c>href</c>, <c>pathname</c>, <c>search</c> and
/// <c>hash</c> -- shared by the Location and the History of the document, because both move it: a fragment
/// navigation its fragment, <c>pushState</c> and <c>replaceState</c> its path and query too, a history
/// traversal any of them. Nothing else changes it: on any other navigation the document did not change, so
/// neither did its URL.
/// </summary>
internal sealed class DocumentUrl
{
    internal DocumentUrl(string href) => Set(href);

    /// <summary>The whole URL.</summary>
    internal string Href { get; private set; } = string.Empty;

    /// <summary>The fragment of an absolute URL, with its <c>#</c>, and nothing at all otherwise.</summary>
    internal string Fragment { get; private set; } = string.Empty;

    /// <summary>Whether the URL is absolute, which a document attached with no URL's is not.</summary>
    internal bool IsAbsolute => Uri.TryCreate(Href, UriKind.Absolute, out _);

    /// <summary>The path of an absolute URL, and nothing otherwise.</summary>
    internal string PathName => Uri.TryCreate(Href, UriKind.Absolute, out var uri) ? uri.AbsolutePath : string.Empty;

    /// <summary>The query of an absolute URL, with its <c>?</c>, and nothing otherwise.</summary>
    internal string Search => Uri.TryCreate(Href, UriKind.Absolute, out var uri) ? uri.Query : string.Empty;

    /// <summary>Moves the document to <paramref name="href"/>.</summary>
    internal void Set(string href)
    {
        Href = href;
        Fragment = Uri.TryCreate(href, UriKind.Absolute, out var uri) ? uri.Fragment : string.Empty;
    }

    /// <summary>Moves the document to the fragment <paramref name="resolved"/> names.</summary>
    internal void MoveToFragment(Uri resolved)
    {
        Href = resolved.ToString();
        Fragment = resolved.Fragment;
    }
}
