using System;
using Broiler.HtmlBridge.Logging;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// <c>window.location</c> / <c>document.location</c> — the URL components a page reads, and the
/// navigation methods it calls.
/// <para>
/// The components were here and the methods were not, so <c>location.replace(url)</c> was
/// <c>undefined is not a function</c>: a TypeError raised at the call, which aborts the rest of the
/// caller rather than the one line. Google Search reaches it on the path its bot-detection
/// bootstrap takes when <c>window.prs</c> is absent — <c>W(a)</c> ends in
/// <c>b !== void 0 &amp;&amp; a.replace(b)</c> over <c>a = location</c> — and so does any page whose
/// fallback is to navigate.
/// </para>
/// <para>
/// <b>A cross-document navigation does not happen, and does not pretend to.</b> A capture renders
/// the one document it was given, and <c>IDomBridgeRuntime</c> carries no navigation member, so
/// there is no path from a binding up to the host that would load another. Each of <c>href =</c>,
/// <c>assign</c>, <c>replace</c> and <c>reload</c> therefore records what was asked for and
/// returns — which is what a browser that blocks a navigation does too, and unlike a throw it
/// leaves the calling script running. Reporting it keeps the request visible: a page that ends by
/// navigating away renders as whatever it had built by then, and the log line is the difference
/// between reading that as the page and reading it as a page that left.
/// </para>
/// <para>
/// <b>A fragment navigation is the exception, because it is not a load.</b> When the target
/// resolves to this document's URL differing only after the <c>#</c>, HTML §7.4.5 calls for
/// "navigate to a fragment": nothing is fetched, the document's URL takes the new fragment and
/// <c>hashchange</c> fires. That much a binding can do, so it does, and all four spellings —
/// <c>location.hash = x</c>, <c>location.href = "#x"</c>, <c>assign("#x")</c> and
/// <c>replace("#x")</c> — take the one path, with <c>href</c> and <c>hash</c> answering the new
/// fragment afterwards. <c>hash</c> was a plain data property before this, which is how the four
/// disagreed: the write stuck and fired nothing, the other three did nothing at all, and
/// <c>href</c> went on ending in the old fragment.
/// </para>
/// <para>
/// Detection is deliberately conservative — a target that does not resolve to this same document is
/// logged and dropped as before. Under-reading a fragment navigation costs one debug line;
/// over-reading a real one would hide the fact that the capture stayed put, which is the whole
/// value of the log.
/// </para>
/// <para>
/// Two parts of the fragment case are still missing. The document is not scrolled to the named
/// anchor. And <c>window.onhashchange</c> is not invoked — only listeners added with
/// <c>addEventListener("hashchange", …)</c> run, because this engine has no general
/// event-handler-IDL-attribute path (<c>window.onload</c> is special-cased in
/// <c>DomBridge.WindowLoad</c>), and a null-valued <c>onhashchange</c> slot that never fired would
/// answer <c>'onhashchange' in window</c> with a promise it does not keep.
/// </para>
/// <para>
/// The other URL components are deliberately NOT updated to the target. On anything but a fragment
/// navigation the document did not change, so neither did its origin, host or path, and a
/// <c>location.pathname</c> that answers for a document nobody loaded is a harder thing to debug
/// than one that answers for the document actually in hand.
/// </para>
/// </summary>
internal static class LocationBinding
{
    private const string LogContext = "DomBridge.location";

    /// <summary>
    /// The URL of the document in hand, behind <c>href</c> and <c>hash</c>. Mutable because a
    /// fragment navigation changes this document's URL without loading another one; every other
    /// navigation leaves it exactly as it was.
    /// </summary>
    private sealed class DocumentUrl
    {
        internal DocumentUrl(string href)
        {
            Href = href;
            // The fragment of an absolute URL and nothing at all otherwise — matching what the two
            // call sites defined for `hash` before this class held it.
            Fragment = Uri.TryCreate(href, UriKind.Absolute, out var uri) ? uri.Fragment : string.Empty;
        }

        internal string Href { get; private set; }

        internal string Fragment { get; private set; }

        internal void MoveToFragment(Uri resolved)
        {
            Href = resolved.ToString();
            Fragment = resolved.Fragment;
        }
    }

    /// <summary>
    /// Builds the Location for a document at <paramref name="href"/>. The components are derived
    /// from the URL when it is absolute; when it is not, only what can be known is defined —
    /// matching what the two call sites did before this module existed.
    /// </summary>
    internal static JSObject Build(string href)
    {
        var location = new JSObject();

        if (Uri.TryCreate(href, UriKind.Absolute, out var uri))
        {
            Add(location, "protocol", uri.Scheme + ":");
            Add(location, "host", Scripting.Origin.HostOf(uri));
            Add(location, "hostname", uri.Host);
            // A URL with no explicit port has an empty `port`, not its scheme's default: the
            // default is what `host` omits, and a page testing `location.port === ""` is asking
            // exactly that question.
            Add(location, "port", uri.IsDefaultPort ? string.Empty : uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Add(location, "pathname", uri.AbsolutePath);
            Add(location, "search", uri.Query);
            Add(location, "origin", Scripting.Origin.Of(uri));
        }
        else
        {
            Add(location, "search", string.Empty);
        }

        // `hash` is not added here — AddNavigationSurface owns it, because a fragment navigation has
        // to move it and `href` together and a data property cannot be kept in step.
        //
        // No window is passed: this overload builds a *frame's* Location, and hashchange belongs to
        // that frame's own event target, which the window-dispatch contract does not reach. The
        // frame's `href` and `hash` still move; only the event is missing.
        AddNavigationSurface(location, href);
        return location;
    }

    /// <summary>
    /// Adds <c>href</c>, <c>hash</c>, <c>assign</c>, <c>replace</c>, <c>reload</c> and
    /// <c>toString</c> to a Location whose remaining components a caller has already defined itself.
    /// <paramref name="window"/> receives <c>hashchange</c> on a fragment navigation; a caller with
    /// no window of its own passes none, and the navigation happens silently.
    /// </summary>
    internal static void AddNavigationSurface(JSObject location, string href, IWindowEventTargetHost? window = null)
    {
        var url = new DocumentUrl(href);

        // `href` is the fourth way to ask for a navigation and the one pages reach for most —
        // `location.href = url` is assign(url) with different spelling (HTML §7.10.5: the setter
        // performs "location-object navigate"). It was a plain data property, so the write stuck
        // and nothing else happened: the page believed it had left, the capture did not know it
        // had been asked, and the URL then disagreed with the document still in hand. As an
        // accessor it answers the document's own URL and routes the write where assign() goes.
        location.FastAddProperty(
            "href",
            new DomFunction((in _) => new JSString(url.Href), "get href"),
            new DomFunction((in a) => Navigate(url, window, "href", in a), "set href"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        // `hash` is an accessor for the same reason, and for one more: `location.hash = "#x"` is a
        // navigation this engine can actually perform, so its setter is the one place here that has
        // to do more than record.
        location.FastAddProperty(
            "hash",
            new DomFunction((in _) => new JSString(url.Fragment), "get hash"),
            new DomFunction((in a) => SetHash(url, window, in a), "set hash"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        location.FastAddValue(
            "assign",
            new DomFunction((in a) => Navigate(url, window, "assign", in a), "assign", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);
        location.FastAddValue(
            "replace",
            new DomFunction((in a) => Navigate(url, window, "replace", in a), "replace", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);
        location.FastAddValue(
            "reload",
            new DomFunction((in _) =>
            {
                RenderLogger.LogDebug(LogCategory.JavaScript, LogContext, $"location.reload() requested; the capture renders the document it was given and does not reload {url.Href}");
                return JSUndefined.Value;
            }, "reload", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);

        // Location stringifies to its href, not to "[object Object]". Pages build URLs with
        // `"" + location` and log it, and the default Object.prototype.toString made both useless.
        location.FastAddValue(
            "toString",
            new DomFunction((in _) => new JSString(url.Href), "toString", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);
    }

    private static JSValue SetHash(DocumentUrl url, IWindowEventTargetHost? window, in Arguments a)
    {
        var value = a.Length > 0 ? a[0].ToString() : string.Empty;
        // "foo" and "#foo" name the same fragment: the "#" is part of the spelling, not of the
        // value, and HTML §7.10.5 prepends it when the page left it off.
        return NavigateTo(url, window, "hash", value.StartsWith('#') ? value : "#" + value);
    }

    private static JSValue Navigate(DocumentUrl url, IWindowEventTargetHost? window, string method, in Arguments a)
        => NavigateTo(url, window, method, a.Length > 0 ? a[0].ToString() : string.Empty);

    private static JSValue NavigateTo(DocumentUrl url, IWindowEventTargetHost? window, string method, string requested)
    {
        var target = requested;

        // Resolved against the document, so the log names the URL the page meant rather than the
        // relative fragment it wrote — and so a fragment navigation can be recognised at all.
        if (Uri.TryCreate(url.Href, UriKind.Absolute, out var baseUri)
            && Uri.TryCreate(baseUri, requested, out var resolved))
        {
            target = resolved.ToString();

            if (IsFragmentNavigation(baseUri, resolved, requested))
            {
                var from = url.Href;
                // hashchange fires only when the fragment actually changed (HTML §7.4.5). A
                // navigation to the fragment already in hand is still same-document, and still not
                // a load, so it moves nothing and announces nothing.
                var changed = !string.Equals(url.Fragment, resolved.Fragment, StringComparison.Ordinal);
                url.MoveToFragment(resolved);

                if (changed)
                    FireHashChange(window, from, url.Href);

                RenderLogger.LogDebug(LogCategory.JavaScript, LogContext,
                    $"{Spell(method, target)} is a fragment navigation; the document is unchanged and location.hash is now \"{url.Fragment}\"");
                return JSUndefined.Value;
            }
        }

        RenderLogger.LogDebug(LogCategory.JavaScript, LogContext, $"{Spell(method, target)} requested; the capture renders the document it was given and does not navigate");
        return JSUndefined.Value;
    }

    /// <summary>
    /// Whether <paramref name="resolved"/> names this same document — everything ahead of the
    /// <c>#</c> equal to <paramref name="baseUri"/> — and is asking for a fragment rather than
    /// merely resolving to one.
    /// <para>
    /// That second half is why the raw <paramref name="requested"/> string is here. An empty target
    /// — <c>replace()</c> with no argument, or <c>replace("")</c> — resolves to the base URL with
    /// its fragment dropped, which is a same-document URL nobody asked to navigate to. Read as a
    /// fragment navigation it would silently clear <c>location.hash</c>; read as a cross-document
    /// one it costs a debug line, so it is read as the second.
    /// </para>
    /// </summary>
    private static bool IsFragmentNavigation(Uri baseUri, Uri resolved, string requested)
        => (requested.StartsWith('#') || !string.IsNullOrEmpty(resolved.Fragment))
            && Uri.Compare(
                baseUri,
                resolved,
                UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
                UriFormat.UriEscaped,
                StringComparison.Ordinal) == 0;

    /// <summary>
    /// Fires <c>hashchange</c> at the window. A listener that throws is logged and swallowed: in a
    /// browser the exception belongs to the listener, not to the <c>location.hash = x</c> that
    /// caused the dispatch, and letting it out here would abort the assigning script instead.
    /// </summary>
    private static void FireHashChange(IWindowEventTargetHost? window, string oldUrl, string newUrl)
    {
        if (window == null)
            return;

        var evt = new JSObject();
        evt.FastAddValue("type", new JSString("hashchange"), JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("bubbles", JSBoolean.False, JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("cancelable", JSBoolean.False, JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("oldURL", new JSString(oldUrl), JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("newURL", new JSString(newUrl), JSPropertyAttributes.EnumerableConfigurableValue);

        try
        {
            window.DispatchWindowEvent(evt);
        }
        catch (Exception ex)
        {
            RenderLogger.LogError(LogCategory.JavaScript, LogContext,
                $"Error firing window hashchange listeners: {ex.Message}", ex);
        }
    }

    // Named as the page spelled it — `location.href = x`, `location.hash = x` and
    // `location.assign(x)` are the same operation, and which one a page used is the first thing a
    // reader wants back.
    private static string Spell(string method, string target)
        => method is "href" or "hash" ? $"location.{method} = {target}" : $"location.{method}({target})";

    private static void Add(JSObject location, string name, string value)
        => location.FastAddValue(name, new JSString(value), JSPropertyAttributes.EnumerableConfigurableValue);
}
