using Broiler.Dom;
using Broiler.HtmlBridge.Dom;
using Broiler.JSeal;
using Broiler.HtmlBridge.Logging;
using Broiler.Net.Http;

namespace Broiler.HtmlBridge;

/// <summary>
/// A navigation to a <c>javascript:</c> URL -- <c>location.href = "javascript:…"</c>, a link the user, a key
/// or a script followed -- runs the URL's script in the document it would have navigated, as HTML's
/// "navigate to a javascript: URL" does.
/// </summary>
/// <remarks>
/// <para>
/// <b>Such a link did nothing</b>, or worse: following one handed the host a navigation to the
/// <c>javascript:</c> URL, which took the page away for a link that only meant to run code. The
/// <c>href="javascript:void(0)"</c> link with a click listener is the commonest shape and runs nothing
/// either way; <c>href="javascript:openMenu()"</c> is the one whose script matters.
/// </para>
/// <para>
/// <b>As Chromium runs it, measured.</b> In a later task -- after the script that asked, its microtasks
/// and a <c>setTimeout(…, 0)</c> it queued first -- in the document's own realm, as its script, under its
/// Content-Security-Policy, which must allow inline script. A string result is a document, which replaces
/// the one the URL ran in at its URL, the history entry staying -- <c>javascript:x.textContent = 'y'</c>
/// included, whose assignment answers a string.
/// </para>
/// <para>
/// <b>Only for a script of the document's own origin</b> (HTML: the navigation's initiator must be same
/// origin-domain with the document). Every document here shares one realm, and what keeps a frame of
/// another origin from the page's document and cookies -- or the page from a frame's -- is the window it
/// is handed. A <c>javascript:</c> URL would hand it the other document's own window context: a frame
/// setting <c>top.location</c>, or a page setting the <c>location</c> of a frame of another origin, would
/// run its code as that document's script. Chromium throws a <c>SecurityError</c> at such a
/// <c>location</c> navigation and runs nothing for such a link (measured); so does this.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    /// <summary>Whether <paramref name="url"/> is a <c>javascript:</c> URL.</summary>
    internal static bool IsJavaScriptUrl(string url) => url.TrimStart().StartsWith("javascript:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Queues the script of the <c>javascript:</c> URL <paramref name="url"/> to run in the page, or in
    /// <paramref name="frame"/>'s document -- unless that has changed by then. Answers whether it was
    /// queued: not when <paramref name="url"/> is not one, nor when <paramref name="initiator"/>, the
    /// document whose script navigated, has another origin than the document it would run in.
    /// </summary>
    /// <param name="initiator">The document whose script navigated; <see langword="null"/> for the user's own navigation.</param>
    internal bool RunJavaScriptUrl(string url, DomElement? frame, DocumentRequestContext? initiator)
    {
        if (_realm is null || !IsJavaScriptUrl(url))
            return false;

        if (initiator is not null && AreCrossOriginForAccess(frame is null ? TopDocumentContext : FrameDocumentContext(frame), initiator))
        {
            RenderLogger.LogWarning(LogCategory.JavaScript, "DomBridge.javascript-url",
                "A javascript: URL is not run: the document whose script navigated to it has another origin");
            return false;
        }

        var document = frame is null ? _document : GetContentDocument(frame);
        _eventLoop.QueueTask(() =>
        {
            if (_realm is null || frame is not null && (!frame.IsConnected || !ReferenceEquals(GetContentDocument(frame), document)))
                return;

            var allowed = frame is null
                ? Csp is null || Csp.AllowsInlineScript()
                : document is not null && _frameScriptPolicies.TryGetValue(document, out var policies)
                    ? policies.AllowsInlineScript()
                    : Csp is null || Csp.AllowsInlineScript();
            if (!allowed)
            {
                RenderLogger.LogDebug(LogCategory.JavaScript, "DomBridge.javascript-url",
                    "A javascript: URL is not run: the document's Content-Security-Policy does not allow inline script");
                return;
            }

            var source = ScriptOf(url);
            void Evaluate()
            {
                try
                {
                    var result = Realm.EvaluateClassicScript(source, "javascript-url");
                    if (result.IsString)
                        ReplaceDocumentWith(frame, result.AsString!);
                }
                catch (Exception ex)
                {
                    RenderLogger.LogWarning(LogCategory.JavaScript, "DomBridge.javascript-url", $"A javascript: URL's script failed: {ex.Message}", ex);
                }
            }

            if (frame is not null && _browsingContexts.TryGetSubWindow(frame, out var window) && window.IsObject)
                RunWithWindowContext(window, Evaluate);
            else
                Evaluate();
        });

        return true;
    }

    /// <summary>The script of a <c>javascript:</c> URL: what follows the scheme, percent-decoded.</summary>
    /// <summary>
    /// Replaces the document a <c>javascript:</c> URL ran in with <paramref name="html"/>, what its script
    /// answered: the page's through the host, a frame's here; at the URL each shows, as Chromium does (measured).
    /// </summary>
    private void ReplaceDocumentWith(DomElement? frame, string html)
    {
        if (frame is null)
        {
            RequestNavigation(new NavigationRequest(CurrentPageUrl, NavigationKind.Replace) { Document = html, Initiator = TopDocumentContext });
            return;
        }

        var url = _browsingContexts.TryGetLocation(frame, out var location) ? location : "about:blank";
        RequestFrameNavigation(frame, new NavigationRequest(url, NavigationKind.Replace), html: html);
    }

    private static string ScriptOf(string url)
    {
        var encoded = url.TrimStart()["javascript:".Length..];
        try
        {
            return Uri.UnescapeDataString(encoded);
        }
        catch (UriFormatException)
        {
            return encoded;
        }
    }
}
