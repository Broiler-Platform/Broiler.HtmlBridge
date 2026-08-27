using System;
using Broiler.HtmlBridge.Logging;
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
/// <b>They do not navigate, and they do not pretend to.</b> A capture renders the one document it
/// was given; there is no path from a binding up to the host that would load another, and
/// <c>--follow-first-link</c> is the explicit opt-in for going somewhere else. So each records what
/// was asked for and returns — which is what a browser that blocks a navigation does too, and
/// unlike a throw it leaves the calling script running. Reporting it keeps the request visible: a
/// page that ends by navigating away renders as whatever it had built by then, and the log line is
/// the difference between reading that as the page and reading it as a page that left.
/// </para>
/// <para>
/// The URL components are deliberately NOT updated to the target. The document did not change, so
/// neither did its URL, and a <c>location.pathname</c> that answers for a document nobody loaded is
/// a harder thing to debug than one that answers for the document actually in hand.
/// </para>
/// </summary>
internal static class LocationBinding
{
    private const string LogContext = "DomBridge.location";

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
            Add(location, "hash", uri.Fragment);
            Add(location, "origin", Scripting.Origin.Of(uri));
        }
        else
        {
            Add(location, "search", string.Empty);
            Add(location, "hash", string.Empty);
        }

        AddNavigationSurface(location, href);
        return location;
    }

    /// <summary>
    /// Adds <c>href</c>, <c>assign</c>, <c>replace</c>, <c>reload</c> and <c>toString</c> to a
    /// Location whose remaining components a caller has already defined itself.
    /// </summary>
    internal static void AddNavigationSurface(JSObject location, string href)
    {
        // `href` is the fourth way to ask for a navigation and the one pages reach for most —
        // `location.href = url` is assign(url) with different spelling (HTML §7.10.5: the setter
        // performs "location-object navigate"). It was a plain data property, so the write stuck
        // and nothing else happened: the page believed it had left, the capture did not know it
        // had been asked, and the URL then disagreed with the document still in hand. As an
        // accessor it answers the document's own URL and routes the write where assign() goes.
        location.FastAddProperty(
            "href",
            new DomFunction((in _) => new JSString(href), "get href"),
            new DomFunction((in a) => Navigate(href, "href", in a), "set href"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        location.FastAddValue(
            "assign",
            new DomFunction((in a) => Navigate(href, "assign", in a), "assign", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);
        location.FastAddValue(
            "replace",
            new DomFunction((in a) => Navigate(href, "replace", in a), "replace", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);
        location.FastAddValue(
            "reload",
            new DomFunction((in _) =>
            {
                RenderLogger.LogDebug(LogCategory.JavaScript, LogContext, $"location.reload() requested; the capture renders the document it was given and does not reload {href}");
                return JSUndefined.Value;
            }, "reload", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);

        // Location stringifies to its href, not to "[object Object]". Pages build URLs with
        // `"" + location` and log it, and the default Object.prototype.toString made both useless.
        location.FastAddValue(
            "toString",
            new DomFunction((in _) => new JSString(href), "toString", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);
    }

    private static JSValue Navigate(string href, string method, in Arguments a)
    {
        var target = a.Length > 0 ? a[0].ToString() : string.Empty;
        // Resolved against the document, so the log names the URL the page meant rather than the
        // relative fragment it wrote.
        if (Uri.TryCreate(href, UriKind.Absolute, out var baseUri)
            && Uri.TryCreate(baseUri, target, out var resolved))
        {
            target = resolved.ToString();
        }

        // Named as the page spelled it — `location.href = x` and `location.assign(x)` are the same
        // operation, and which one a page used is the first thing a reader wants back.
        var request = method == "href" ? $"location.href = {target}" : $"location.{method}({target})";
        RenderLogger.LogDebug(LogCategory.JavaScript, LogContext, $"{request} requested; the capture renders the document it was given and does not navigate");
        return JSUndefined.Value;
    }

    private static void Add(JSObject location, string name, string value)
        => location.FastAddValue(name, new JSString(value), JSPropertyAttributes.EnumerableConfigurableValue);
}
