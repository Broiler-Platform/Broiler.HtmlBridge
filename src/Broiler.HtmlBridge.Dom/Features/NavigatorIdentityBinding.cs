using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The <c>navigator</c> members that report who the browser is and what the machine underneath it
/// has — the legacy identity constants (<c>appCodeName</c>, <c>appName</c>, <c>appVersion</c>,
/// <c>product</c>, <c>productSub</c>), <c>webdriver</c>, and the hardware pair
/// <c>hardwareConcurrency</c> / <c>deviceMemory</c> plus <c>maxTouchPoints</c>. The sibling of
/// <see cref="NavigatorCapabilityBinding"/>, which answers what the host machine can *do*.
/// </summary>
/// <remarks>
/// <para>
/// All of these read <c>undefined</c> before this, and <c>undefined</c> is the one answer none of
/// them is allowed to have: five are constants the specification *mandates* for every user agent,
/// and the rest are read inside arithmetic and comparisons where an absent value propagates rather
/// than announcing itself. <c>navigator.appVersion.indexOf(…)</c> — still the shape of a great deal
/// of legacy sniffing — threw outright.
/// </para>
/// <para>
/// The five legacy constants are not identity claims about a vendor. HTML §8.9 fixes them at
/// <c>"Mozilla"</c>, <c>"Netscape"</c> and <c>"Gecko"</c> for <em>every</em> browser regardless of
/// engine, precisely so that sniffing them tells a page nothing; returning anything else would be
/// the deviation. <c>appVersion</c> is derived from the one user-agent string rather than written
/// out a second time, so it cannot drift from <c>navigator.userAgent</c> — the same rule the
/// userAgent registration already follows against the network's copy.
/// </para>
/// <para>
/// <c>vendor</c> is deliberately left as it was. The specification permits exactly
/// <c>""</c>, <c>"Apple Computer, Inc."</c> or <c>"Google Inc."</c>, Broiler's user agent does not
/// claim to be Chrome, and <c>""</c> is both a conforming value and the truthful one; changing it to
/// match Chromium's answer would be an identity claim, not a fix.
/// </para>
/// <para>
/// <c>webdriver</c> is <c>true</c>, which is the honest answer rather than the flattering one:
/// the attribute reports whether the user agent is controlled by automation, and a capture engine
/// is exactly that. Reporting <c>false</c> would be claiming to be a human-driven browser.
/// </para>
/// <para>
/// The hardware pair is measured, not asserted. <c>hardwareConcurrency</c> is the machine's real
/// logical processor count, and <c>deviceMemory</c> is its real memory rounded the way the Device
/// Memory specification requires — to a power of two, then clamped to the 0.25–8 range it allows,
/// which is the deliberate coarsening that keeps the value from being a fingerprint.
/// <c>maxTouchPoints</c> is <c>0</c> because a capture has no touch input at all.
/// </para>
/// <para>
/// The object-valued surfaces alongside these — <c>connection</c>, <c>permissions</c>,
/// <c>storage</c>, <c>mediaDevices</c>, <c>mediaCapabilities</c>, <c>userAgentData</c> — are whole
/// APIs rather than values, and are deliberately not added here: each needs its own decision about
/// whether a present-but-empty object answers a page's <c>'x' in navigator</c> detection more
/// misleadingly than absence does, which is the same test that kept <c>speechSynthesis</c> and
/// <c>navigator.bluetooth</c> out.
/// </para>
/// </remarks>
internal static class NavigatorIdentityBinding
{
    /// <summary>Installs the identity constants, <c>webdriver</c>, and the hardware members.</summary>
    /// <param name="navigator">The navigator object being built.</param>
    /// <param name="userAgent">The single user-agent string the rest of the bridge reports.</param>
    public static void Install(JSObject navigator, string userAgent)
    {
        // HTML §8.9 pins these three for every user agent, whatever the engine.
        Add(navigator, "appCodeName", new JSString("Mozilla"));
        Add(navigator, "appName", new JSString("Netscape"));
        Add(navigator, "product", new JSString("Gecko"));

        // §8.9 permits "20030107" or "20100101" and nothing else; the former is what every
        // non-Gecko engine returns.
        Add(navigator, "productSub", new JSString("20030107"));

        // "The user agent string with any leading "Mozilla/" removed" (§8.9). Derived so the two
        // cannot disagree.
        Add(navigator, "appVersion", new JSString(
            userAgent.StartsWith("Mozilla/", StringComparison.Ordinal) ? userAgent["Mozilla/".Length..] : userAgent));

        // True: this user agent is driven by automation, which is what the attribute reports.
        Add(navigator, "webdriver", JSBoolean.True);

        // A capture has no touch input.
        Add(navigator, "maxTouchPoints", new JSNumber(0));

        // Measured from the machine actually running the capture.
        Add(navigator, "hardwareConcurrency", new JSNumber(Math.Max(1, Environment.ProcessorCount)));
        Add(navigator, "deviceMemory", new JSNumber(ApproximateDeviceMemoryGiB()));
    }

    private static void Add(JSObject navigator, string name, JSValue value)
        => navigator.FastAddValue(name, value, JSPropertyAttributes.EnumerableConfigurableValue);

    /// <summary>
    /// The machine's memory in GiB, rounded down to the nearest power of two and clamped to
    /// 0.25–8 — the coarsening the Device Memory specification mandates so the value cannot be used
    /// as a precise fingerprint.
    /// </summary>
    private static double ApproximateDeviceMemoryGiB()
    {
        var totalBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (totalBytes <= 0)
            return 8; // Nothing to measure; report the top of the allowed range rather than 0.

        var gib = totalBytes / (1024d * 1024d * 1024d);

        // Largest allowed power of two not exceeding the real value.
        double[] steps = [0.25, 0.5, 1, 2, 4, 8];
        var result = steps[0];
        foreach (var step in steps)
        {
            if (gib >= step)
                result = step;
        }

        return result;
    }
}
