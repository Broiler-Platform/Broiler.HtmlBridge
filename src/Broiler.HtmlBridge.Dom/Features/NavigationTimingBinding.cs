using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Net;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The document's <c>PerformanceNavigationTiming</c> entry and the three <c>performance</c>
/// accessors that hand it out — <c>getEntries</c>, <c>getEntriesByType</c> and
/// <c>getEntriesByName</c> (Navigation Timing Level 2 §4, Performance Timeline §3). Pure static:
/// the entry is built from the page URL the bridge already knows and the protocol its loader
/// negotiates, and nothing here touches bridge state afterwards.
/// </summary>
/// <remarks>
/// <para>
/// <c>performance.getEntriesByType</c> answered with an empty array and <c>getEntries</c> did not
/// exist at all, so the standard way to reach this entry —
/// <c>performance.getEntries()[0].nextHopProtocol</c>, or
/// <c>getEntriesByType('navigation')[0]</c> — was a TypeError on <c>undefined</c> rather than a
/// missing measurement. That aborts the function that asked, which for the analytics preambles
/// that read navigation timing is the same function that goes on to install the rest of the page's
/// instrumentation.
/// </para>
/// <para>
/// <b>Where each mark comes from.</b> The document-lifecycle half — <c>domInteractive</c> through
/// <c>loadEventEnd</c> — is stamped by the bridge's own load sequence through
/// <see cref="NavigationTimingState"/>. The network half — <c>fetchStart</c> through
/// <c>responseEnd</c>, and the body-size trio — cannot be observed from here at all, because the
/// document is fetched before this bridge exists; it is measured by the host and handed across as a
/// <see cref="DocumentFetchTiming"/>, along with the time origin taken at the navigation's start
/// that makes the two halves one timeline. A host that measures no fetch supplies none, and the
/// network marks then report the specification's "not observed" <c>0</c>.
/// </para>
/// <para>
/// <b>What this entry deliberately does not carry.</b> <c>startTime</c> is fixed at 0 rather than
/// measured, by definition — the time origin <em>is</em> this navigation's start, which is also what
/// makes <c>duration</c> equal to <c>loadEventEnd</c> outright. Paint timings, resource entries and
/// user marks belong to buffers a capture does not keep, so the entry-list getters answer with an
/// empty list for them rather than inventing one.
/// </para>
/// <para>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>): the realm mints the entry, the
/// three accessors and every array they answer with, so nothing here names an engine type. The
/// entry object itself is captured by the accessors exactly as before — one entry per document,
/// handed out by reference, so a page that compares two results gets the same object.
/// </para>
/// </remarks>
internal static class NavigationTimingBinding
{
    /// <summary>
    /// Installs the entry-list accessors on <paramref name="performance"/>.
    /// </summary>
    /// <param name="realm">The realm the entry, the accessors and their arrays belong to.</param>
    /// <param name="performance">The <c>performance</c> object being built.</param>
    /// <param name="pageUrl">The document's URL, or the empty string when it has none.</param>
    /// <param name="pageProtocol">
    /// The document URL's scheme with its colon (<c>"https:"</c>), as <c>location.protocol</c>
    /// reports it. It decides whether there was a network hop to name.
    /// </param>
    /// <param name="timing">
    /// The document-lifecycle marks, stamped by the load sequence after this runs. Passed in rather
    /// than read here because the entry is built before any of them has happened.
    /// </param>
    /// <param name="fetchTiming">
    /// The host's measurements of the document fetch, or <see langword="null"/> when the document
    /// did not come through a host that measures one (HTML handed to the bridge as a string, the
    /// conformance runner, a test). Absent, the network phases keep reporting the specification's
    /// "not observed" <c>0</c>.
    /// </param>
    public static void Install(
        IJsRealm realm,
        JsValue performance,
        string pageUrl,
        string pageProtocol,
        NavigationTimingState timing,
        DocumentFetchTiming? fetchTiming)
    {
        var navigation = BuildNavigationEntry(realm, pageUrl, pageProtocol, timing, fetchTiming);

        realm.DefineValue(performance, "getEntries",
            realm.NewMethod("getEntries", (in call) => call.Realm.NewArray([navigation]), 0));

        realm.DefineValue(performance, "getEntriesByType",
            realm.NewMethod("getEntriesByType", (in call) => EntriesByType(navigation, in call), 1));

        realm.DefineValue(performance, "getEntriesByName",
            realm.NewMethod("getEntriesByName", (in call) => EntriesByName(navigation, pageUrl, in call), 1));
    }

    /// <summary>
    /// The ALPN token for the hop that fetched this document, or the empty string when there was no
    /// network hop to name — which is what the specification asks for on a <c>file:</c> or
    /// <c>about:</c> document, and on HTML the bridge was handed directly as a string.
    /// </summary>
    private static string NextHopProtocol(string pageProtocol) =>
        pageProtocol is "http:" or "https:" ? Layout.Net.BroilerHttpProtocol.NextHopProtocol : string.Empty;

    private static JsValue BuildNavigationEntry(
        IJsRealm realm,
        string pageUrl,
        string pageProtocol,
        NavigationTimingState timing,
        DocumentFetchTiming? fetchTiming)
    {
        var entry = realm.NewObject();

        // PerformanceEntry (Performance Timeline §3.1). A navigation entry is named by the
        // document's URL and its startTime is 0 by definition — the time origin *is* this
        // navigation's start.
        realm.DefineValue(entry, "name", JsValue.String(pageUrl));
        realm.DefineValue(entry, "entryType", JsValue.String("navigation"));
        realm.DefineValue(entry, "startTime", JsValue.Number(0));

        // duration is loadEventEnd - startTime (Navigation Timing §4), and startTime is 0 for a
        // navigation entry, so it *is* loadEventEnd. It was a hardcoded 0, which is right only until
        // the load event ends — and a page reads this after load, where 0 is the one answer it cannot
        // be. `entry.duration` is how the shortest form of "how long did this page take" is written,
        // so reporting 0 was worse than reporting nothing: it is a plausible number rather than an
        // absent one. An accessor, not a value, for the same reason the marks are: the entry is built
        // before the load sequence runs, and reads 0 until it reaches the end of it.
        AddTimingAccessor(realm, entry, "duration", () => timing.LoadEventEnd);

        // PerformanceResourceTiming (Resource Timing §4.1), of which a navigation entry is a
        // subtype. `initiatorType` is fixed at "navigation" for one, and the protocol is the engine's
        // own — see BroilerHttpProtocol, which pins the request version this names.
        realm.DefineValue(entry, "initiatorType", JsValue.String("navigation"));
        realm.DefineValue(entry, "nextHopProtocol", JsValue.String(NextHopProtocol(pageProtocol)));

        // PerformanceNavigationTiming (Navigation Timing §4). "navigate" is the plain case — the
        // alternatives ("reload", "back_forward", "prerender") describe entries into a session
        // history Broiler does not keep, so none of them can arise here.
        realm.DefineValue(entry, "type", JsValue.String("navigate"));

        // Redirects are followed inside the HTTP client, which does not report how many it took, so
        // this is the count Broiler can vouch for rather than a measurement.
        realm.DefineValue(entry, "redirectCount", JsValue.Number(0));

        // --- Timing attributes (Navigation Timing §4). ---
        //
        // These were absent entirely, and absent is the one thing they may not be: they are read
        // inside subtraction far more often than alone, so the RUM idioms built on them
        // (`responseEnd - requestStart`, `domComplete - domInteractive`) produced NaN rather than a
        // duration, silently.
        //
        // The document-lifecycle half is MEASURED, from the same monotonic clock and time origin
        // performance.now() uses, so a page can compare a mark against a now() reading and get two
        // points on one timeline. Each reads 0 until its moment is reached, which is what the
        // specification says an unreached mark reports.
        AddTimingAccessor(realm, entry, "domInteractive", () => timing.DomInteractive);
        AddTimingAccessor(realm, entry, "domContentLoadedEventStart", () => timing.DomContentLoadedEventStart);
        AddTimingAccessor(realm, entry, "domContentLoadedEventEnd", () => timing.DomContentLoadedEventEnd);
        AddTimingAccessor(realm, entry, "domComplete", () => timing.DomComplete);
        AddTimingAccessor(realm, entry, "loadEventStart", () => timing.LoadEventStart);
        AddTimingAccessor(realm, entry, "loadEventEnd", () => timing.LoadEventEnd);

        // These are 0 because the phase genuinely did not happen, which is the value the
        // specification gives for each: nothing redirected, there is no previous document to unload,
        // and no service worker intercepted the navigation.
        AddTimingConstant(realm, entry, "redirectStart", 0);
        AddTimingConstant(realm, entry, "redirectEnd", 0);
        AddTimingConstant(realm, entry, "unloadEventStart", 0);
        AddTimingConstant(realm, entry, "unloadEventEnd", 0);
        AddTimingConstant(realm, entry, "workerStart", 0);

        // The network phases, measured when the host handed its document-fetch timings across and
        // 0 — the specification's "not observed" — when it did not.
        //
        // The document is retrieved by the capture host before the bridge exists, so nothing at this
        // layer can observe the DNS, connect, request or response boundaries itself; the host that
        // performs the fetch measures them and passes them in with the time origin it took at the
        // navigation's start. Without that origin these could not be expressed at all: the bridge's
        // own origin is stamped when the performance object is built, which is already after the
        // fetch, so every real value would be negative and 0 is the floor the specification clamps
        // to. That is why these were 0 rather than approximate — they are present so the arithmetic
        // above yields a number instead of NaN, and a page cannot tell an unmeasured phase from an
        // instantaneous one, which is the cost of the honest answer here.
        //
        // A phase a fetch did not perform reports the previous phase rather than 0 (see
        // DocumentFetchTiming): a file: document does no DNS lookup and opens no connection, so its
        // lookup and connect marks collapse onto fetchStart. secureConnectionStart is the exception
        // and stays 0 when no TLS handshake happened.
        AddTimingConstant(realm, entry, "fetchStart", fetchTiming?.FetchStart ?? 0);
        AddTimingConstant(realm, entry, "domainLookupStart", fetchTiming?.DomainLookupStart ?? 0);
        AddTimingConstant(realm, entry, "domainLookupEnd", fetchTiming?.DomainLookupEnd ?? 0);
        AddTimingConstant(realm, entry, "connectStart", fetchTiming?.ConnectStart ?? 0);
        AddTimingConstant(realm, entry, "connectEnd", fetchTiming?.ConnectEnd ?? 0);
        AddTimingConstant(realm, entry, "secureConnectionStart", fetchTiming?.SecureConnectionStart ?? 0);
        AddTimingConstant(realm, entry, "requestStart", fetchTiming?.RequestStart ?? 0);
        AddTimingConstant(realm, entry, "responseStart", fetchTiming?.ResponseStart ?? 0);
        AddTimingConstant(realm, entry, "responseEnd", fetchTiming?.ResponseEnd ?? 0);

        // Resource Timing's body-size trio, from the same measurement. 0 is its documented "not
        // available" value, which is still the answer when no fetch was measured.
        AddTimingConstant(realm, entry, "transferSize", fetchTiming?.TransferSize ?? 0);
        AddTimingConstant(realm, entry, "encodedBodySize", fetchTiming?.EncodedBodySize ?? 0);
        AddTimingConstant(realm, entry, "decodedBodySize", fetchTiming?.DecodedBodySize ?? 0);

        realm.DefineValue(entry, "toJSON", realm.NewMethod("toJSON", (in _) => entry, 0));

        return entry;
    }

    private static JsValue EntriesByType(JsValue navigation, in JsCall call)
    {
        // ToJsString rather than the handle's rendering: getEntriesByType(obj) coerces its argument,
        // and an object's own toString is what decides which type was asked for.
        string type = call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty;
        return string.Equals(type, "navigation", StringComparison.Ordinal)
            ? call.Realm.NewArray([navigation])
            : call.Realm.NewArray();
    }

    private static JsValue EntriesByName(JsValue navigation, string pageUrl, in JsCall call)
    {
        if (call.Length == 0)
            return call.Realm.NewArray();

        // The second argument narrows by entry type; a caller that passes one that is not
        // "navigation" is asking for entries this timeline has none of.
        if (call.Length > 1 && !call[1].IsUndefined &&
            !string.Equals(call.Realm.ToJsString(call[1]), "navigation", StringComparison.Ordinal))
            return call.Realm.NewArray();

        return string.Equals(call.Realm.ToJsString(call[0]), pageUrl, StringComparison.Ordinal)
            ? call.Realm.NewArray([navigation])
            : call.Realm.NewArray();
    }

    /// <summary>A live timing mark: an accessor so the entry reports the value at read time.</summary>
    /// <remarks>
    /// The getter is a bare delegate: the realm mints the accessor function, names it
    /// <c>get {name}</c> and makes it non-constructable — the three things the bridge's own function
    /// wrapper did at this call site before. A <see langword="null"/> setter is still how read-only
    /// is spelled, and these marks are read-only.
    /// </remarks>
    private static void AddTimingAccessor(IJsRealm realm, JsValue entry, string name, Func<double> read)
        => realm.DefineAccessor(entry, name, (in _) => JsValue.Number(read()), null);

    /// <summary>A mark whose value cannot change for this document.</summary>
    private static void AddTimingConstant(IJsRealm realm, JsValue entry, string name, double value)
        => realm.DefineValue(entry, name, JsValue.Number(value));
}
