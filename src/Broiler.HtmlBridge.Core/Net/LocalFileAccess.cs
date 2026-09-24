using System;

namespace Broiler.HtmlBridge;

/// <summary>
/// Which documents may have the bridge read a <c>file:</c> URL from the local filesystem for them: a
/// script, a module, a stylesheet, a frame's document or a worker script.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only a <c>file:</c> document.</b> A browser never lets a web page load a local file, and here
/// every loader read <c>file:</c> URLs from disk whoever asked: an <c>http(s)</c> page could run a
/// local script as its own, read a local file's text through a frame's <c>contentDocument</c>, or name
/// a UNC path (<c>file://host/share/x.js</c>), which Windows answers by opening an SMB session to that
/// host and offering the user's NTLM credentials. Broiler.HTML's stylesheet, image and font loaders
/// already follow this rule; this is the same rule for the bridge's own loaders.
/// </para>
/// <para>
/// <b>And a caller with no document.</b> A tool that runs script without naming a page -- no URL at
/// all, or the <c>about:blank</c> a bridge attached without one stands for -- is reading the files it
/// was pointed at, and keeps doing so.
/// </para>
/// </remarks>
internal static class LocalFileAccess
{
    /// <summary>
    /// Whether the document at <paramref name="documentUrl"/> may read local files:
    /// <see langword="true"/> for a <c>file:</c> document and for no document at all.
    /// </summary>
    internal static bool AllowedFor(Uri? documentUrl) =>
        documentUrl is null ||
        documentUrl.IsAbsoluteUri && (documentUrl.IsFile ||
            string.Equals(documentUrl.AbsoluteUri, "about:blank", StringComparison.OrdinalIgnoreCase));

    /// <summary>As <see cref="AllowedFor(Uri?)"/>, for a document URL given as text (empty: no document).</summary>
    internal static bool AllowedFor(string? documentUrl) =>
        string.IsNullOrWhiteSpace(documentUrl) ||
        Uri.TryCreate(documentUrl, UriKind.Absolute, out var url) && AllowedFor(url);
}
