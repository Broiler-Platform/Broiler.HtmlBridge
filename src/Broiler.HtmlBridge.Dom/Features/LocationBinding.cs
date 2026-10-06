using System;
using Broiler.JSeal;
using Broiler.HtmlBridge.Logging;

// Nothing here is engine-typed.

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
/// <b>A cross-document navigation is asked for here and performed somewhere else.</b> A binding has
/// no loader and no session history, so <c>href =</c>, <c>assign</c>, <c>replace</c> and
/// <c>reload</c> each resolve the target, hand it to the host as a
/// <see cref="NavigationRequest"/>, and return. Returning rather than throwing is the point: a
/// throw would abort the caller exactly as the missing method used to, and the page has more to do
/// before it leaves. The host reads the request once execution settles and decides whether to
/// follow — an interactive browser does, a capture pinned to one document need not.
/// </para>
/// <para>
/// A frame's Location, built by <see cref="Build"/>, has a host of its own, which loads the frame's
/// next document rather than the page's. With no host at all there is nowhere to record the request,
/// so it is logged and dropped, which is what every one of these did before the host surface
/// existed -- and what a frame's did until frames could navigate.
/// </para>
/// <para>
/// <b>A fragment navigation is the exception, because it is not a load.</b> When the target
/// resolves to this document's URL differing only after the <c>#</c>, HTML §7.4.5 calls for
/// "navigate to a fragment": nothing is fetched, the document's URL takes the new fragment, and the
/// window hears <c>popstate</c> at once and <c>hashchange</c> in a later task — which the host fires,
/// since only it can queue a task. That much a binding can do, so it does, and all four spellings —
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
/// One part of the fragment case is still missing: a script's fragment navigation does not scroll the
/// document to the named anchor.
/// </para>
/// <para>
/// The other URL components are deliberately NOT updated to the target. On anything but a fragment
/// navigation the document did not change, so neither did its origin, host or path, and a
/// <c>location.pathname</c> that answers for a document nobody loaded is a harder thing to debug
/// than one that answers for the document actually in hand.
/// </para>
/// <para>
/// <b>One navigation surface, installed two ways, and both through the realm.</b>
/// <c>Registration/Window.cs</c> builds the top-level Location through the realm and passes both to
/// <see cref="AddNavigationSurface(IJsRealm, JsValue, string, ILocationHost?)"/>, which is the whole of
/// that path. A frame's Location, asked for by <c>SubWindowBinding</c>, is
/// <see cref="Build(IJsRealm, string, ILocationHost?)"/>, which takes the realm and is entirely realm-framed.
/// Nothing in this file is left in engine terms. The <em>logic</em> is not
/// duplicated either way: every installer hands the same <see cref="DocumentUrl"/> to the same
/// <see cref="NavigateTo"/>/<see cref="Request"/> pair, and only the six installations and the two
/// argument reads differ.
/// </para>
/// </summary>
internal static class LocationBinding
{
    private const string LogContext = "DomBridge.location";

    /// <summary>
    /// Builds the Location for a document at <paramref name="url"/>. The components are derived
    /// from the URL when it is absolute; when it is not, only what can be known is defined.
    /// </summary>
    /// <param name="realm">The realm the Location is built in.</param>
    /// <param name="url">The URL of the frame's document, which its History moves too.</param>
    /// <param name="host">
    /// The frame's own: a navigation loads another document into the frame, and <c>popstate</c> and
    /// <c>hashchange</c> fire at the frame's window.
    /// </param>
    internal static JsValue Build(IJsRealm realm, DocumentUrl url, ILocationHost? host = null)
    {
        var location = realm.NewObject();

        if (Uri.TryCreate(url.Href, UriKind.Absolute, out var uri))
        {
            Add(realm, location, "protocol", uri.Scheme + ":");
            Add(realm, location, "host", Scripting.Origin.HostOf(uri));
            Add(realm, location, "hostname", uri.Host);
            // A URL with no explicit port has an empty `port`, not its scheme's default: the
            // default is what `host` omits, and a page testing `location.port === ""` is asking
            // exactly that question.
            Add(realm, location, "port", uri.IsDefaultPort ? string.Empty : uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            // The path and the query follow the document's URL, which pushState and replaceState move.
            realm.DefineAccessor(location, "pathname", (in _) => JsValue.String(url.PathName), null);
            realm.DefineAccessor(location, "search", (in _) => JsValue.String(url.Search), null);
            // `origin` is a getter with no setter, and not configurable: HTML's Location has no way to
            // change it, and messaging and the frame's own code must not see a value its script chose.
            var origin = JsValue.String(Scripting.Origin.Of(uri));
            realm.DefineAccessor(location, "origin", (in _) => origin, null, JsPropertyFlags.Enumerable);
        }
        else
        {
            Add(realm, location, "search", string.Empty);
        }

        // `hash` is not added here — the navigation surface owns it, because a fragment navigation
        // has to move it and `href` together and a data property cannot be kept in step.
        AddNavigationSurface(realm, location, url, host);
        return location;
    }

    /// <summary>
    /// Adds <c>href</c>, <c>hash</c>, <c>assign</c>, <c>replace</c>, <c>reload</c> and
    /// <c>toString</c> to a Location whose remaining components a caller has already defined itself.
    /// <paramref name="host"/> takes the cross-document navigations and announces a fragment one; a
    /// caller with no host passes none, and both are logged and dropped.
    /// </summary>
    /// <remarks>
    /// The two argument reads coerce with the realm's <c>ToJsString</c> — the observable ECMAScript
    /// <c>ToString</c>, because <c>location.href = new URL(…)</c> is a page assigning an object with
    /// a <c>toString</c>, and that is what the engine's own <c>ToString()</c> ran on the same
    /// argument before.
    /// </remarks>
    internal static void AddNavigationSurface(
        IJsRealm realm, JsValue location, DocumentUrl url, ILocationHost? host = null)
    {
        // `href` is the fourth way to ask for a navigation and the one pages reach for most —
        // `location.href = url` is assign(url) with different spelling (HTML §7.10.5: the setter
        // performs "location-object navigate"). It was a plain data property, so the write stuck
        // and nothing else happened: the page believed it had left, the capture did not know it
        // had been asked, and the URL then disagreed with the document still in hand. As an
        // accessor it answers the document's own URL and routes the write where assign() goes.
        realm.DefineAccessor(
            location, "href",
            (in _) => JsValue.String(url.Href),
            (in call) => Navigate(url, host, "href", in call));

        // `hash` is an accessor for the same reason, and for one more: `location.hash = "#x"` is a
        // navigation this engine can actually perform, so its setter is the one place here that has
        // to do more than record.
        realm.DefineAccessor(
            location, "hash",
            (in _) => JsValue.String(url.Fragment),
            (in call) => SetHash(url, host, in call));

        realm.DefineMethod(location, "assign", 1, (in call) => Navigate(url, host, "assign", in call));
        realm.DefineMethod(location, "replace", 1, (in call) => Navigate(url, host, "replace", in call));
        realm.DefineMethod(location, "reload", 0, (in _) =>
        {
            Request(host, NavigationKind.Reload, "location.reload()", url.Href);
            return JsValue.Undefined;
        });

        // Location stringifies to its href, not to "[object Object]". Pages build URLs with
        // `"" + location` and log it, and the default Object.prototype.toString made both useless.
        realm.DefineMethod(location, "toString", 0, (in _) => JsValue.String(url.Href));
    }

    private static JsValue SetHash(DocumentUrl url, ILocationHost? host, in JsCall call)
    {
        var value = call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty;
        // "foo" and "#foo" name the same fragment: the "#" is part of the spelling, not of the
        // value, and HTML §7.10.5 prepends it when the page left it off.
        NavigateTo(url, host, "hash", value.StartsWith('#') ? value : "#" + value);
        return JsValue.Undefined;
    }

    private static JsValue Navigate(DocumentUrl url, ILocationHost? host, string method, in JsCall call)
    {
        var requested = call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty;

        // A javascript: URL runs its script in this document rather than loading one, as a task
        // (HTML "navigate to a javascript: URL"); the host queues and runs it -- for a script of this
        // document's origin. Another origin's gets the SecurityError Chromium throws (measured).
        if (requested.TrimStart().StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
        {
            RenderLogger.LogDebug(LogCategory.JavaScript, LogContext, $"{Spell(method, requested)} runs its script");
            if (host is not null && !host.RunJavaScriptUrl(requested.Trim()))
            {
                var member = method == "href" ? "set a named property 'href' on" : $"execute '{method}' on";
                throw call.Realm.DomError("SecurityError",
                    $"Failed to {member} 'Location': The current window does not have permission to navigate the target frame to '{requested}'.");
            }

            return JsValue.Undefined;
        }

        NavigateTo(url, host, method, requested);
        return JsValue.Undefined;
    }

    private static void NavigateTo(DocumentUrl url, ILocationHost? host, string method, string requested)
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
                // popstate and hashchange fire only when the fragment actually changed (HTML §7.4.5;
                // Chromium fires neither for the fragment already in hand). A navigation to the
                // fragment already in hand is still same-document, and still not a load, so it moves
                // nothing and announces nothing.
                var changed = !string.Equals(url.Fragment, resolved.Fragment, StringComparison.Ordinal);
                url.MoveToFragment(resolved);

                // Every fragment navigation looks for its target again, changed or not: an element
                // that has since been given the name is found the second time (HTML §7.4.6.3).
                host?.NavigatedToFragment(url.Fragment);

                if (changed)
                    host?.FragmentChanged(from, url.Href, replace: method == "replace");

                RenderLogger.LogDebug(LogCategory.JavaScript, LogContext,
                    $"{Spell(method, target)} is a fragment navigation; the document is unchanged and location.hash is now \"{url.Fragment}\"");
                return;
            }
        }

        // Setting `hash` is same-document by definition, so it never becomes a request to load
        // something. It reaches here only when the document's own URL does not parse as absolute —
        // an Attach with no URL — and there is no base to resolve a fragment against. Asking the
        // host to load "#x" would be worse than doing nothing, which is what a browser with no
        // document URL to move does anyway.
        if (method == "hash")
        {
            RenderLogger.LogDebug(LogCategory.JavaScript, LogContext,
                $"{Spell(method, target)} ignored; this document has no absolute URL to hang a fragment on");
            return;
        }

        Request(host, KindOf(method), Spell(method, target), target);
    }

    /// <summary>
    /// Hands a cross-document navigation to the host, or — with no host to hand it to — logs it and
    /// drops it, which is what all of these did before the host surface existed.
    /// </summary>
    private static void Request(ILocationHost? host, NavigationKind kind, string spelling, string target)
    {
        if (host == null)
        {
            RenderLogger.LogDebug(LogCategory.JavaScript, LogContext,
                $"{spelling} requested; nothing here can load another document, so {target} is not navigated to");
            return;
        }

        // The document's own URL is unchanged either way: nothing has loaded yet, and `href`
        // answering a target the host may decline would describe a document nobody has.
        RenderLogger.LogDebug(LogCategory.JavaScript, LogContext,
            $"{spelling} requested; {target} handed to the host, which decides whether to follow it");
        host.RequestNavigation(new NavigationRequest(target, kind));
    }

    private static NavigationKind KindOf(string method) => method switch
    {
        "replace" => NavigationKind.Replace,
        _ => NavigationKind.Assign,
    };

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

    // Named as the page spelled it — `location.href = x`, `location.hash = x` and
    // `location.assign(x)` are the same operation, and which one a page used is the first thing a
    // reader wants back.
    private static string Spell(string method, string target)
        => method is "href" or "hash" ? $"location.{method} = {target}" : $"location.{method}({target})";

    /// <summary>One URL component, enumerable and configurable as every Location component is.</summary>
    private static void Add(IJsRealm realm, JsValue location, string name, string value)
        => realm.DefineValue(location, name, JsValue.String(value));
}
