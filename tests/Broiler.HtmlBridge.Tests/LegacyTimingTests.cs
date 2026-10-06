using System.Net;
using Broiler.HtmlBridge.Dom;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// The members reCAPTCHA reads beside the performance timeline: <c>navigator.userActivation</c>, the
/// legacy <c>performance.timing</c> and <c>performance.navigation</c>, <c>performance.toJSON()</c>,
/// <c>window.opener</c> and a frame's <c>document.referrer</c>. Shapes and values are Chromium's,
/// measured, except where a test says otherwise.
/// </summary>
/// <remarks>
/// Each was missing: <c>navigator.userActivation</c> and <c>performance.timing</c> read
/// <c>undefined</c>, so whatever was read through them threw, <c>opener</c> was not there and a frame's
/// document had no <c>referrer</c>.
/// </remarks>
public class LegacyTimingTests
{
    private const string Out = "<div id=\"out\"></div>";

    private static string Run(string script, string body = "")
    {
        var rendered = new ScriptEngine().Execute(
            ["function done(v) { document.getElementById('out').textContent = String(v); }", script],
            $"<!DOCTYPE html><html><head></head><body>{Out}{body}</body></html>",
            "https://example.test/page");
        Assert.NotNull(rendered);
        return PageProbe.OutOf(rendered!, decode: true);
    }

    /// <summary>
    /// <c>navigator.userActivation</c> is one <c>UserActivation</c>, not activated before the user has
    /// done anything; the interface cannot be constructed.
    /// </summary>
    [Fact]
    public void UserActivationIsChromiums()
    {
        Assert.Equal(
            "object,[object UserActivation],false,false,function,constructor,hasBeenActive,isActive,true," +
            "TypeError: Failed to construct 'UserActivation': Illegal constructor",
            Run(
                "var ua = navigator.userActivation, ctor;" +
                "try { new UserActivation(); } catch (e) { ctor = e.name + ': ' + e.message; }" +
                "done([typeof ua, Object.prototype.toString.call(ua), ua.isActive, ua.hasBeenActive, typeof UserActivation," +
                "  Object.getOwnPropertyNames(UserActivation.prototype).sort().join(), navigator.userActivation === ua, ctor].join());"));
    }

    /// <summary>A user's click activates the document: its listener reads it active, and it has been since.</summary>
    [Fact]
    public void AClickActivatesTheDocument()
    {
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions
        {
            LayoutViewFactory = () => new DeclaredBoxLayoutView(new Dictionary<string, System.Drawing.RectangleF>
            {
                ["root"] = new(0, 0, 1024, 768),
                ["b"] = new(10, 10, 80, 30),
            }),
        }));
        using var session = engine.ExecuteInteractive(
            ["var out = document.getElementById('out'), log = [navigator.userActivation.hasBeenActive];" +
             "document.getElementById('b').addEventListener('click', function () {" +
             "  log.push(navigator.userActivation.isActive, navigator.userActivation.hasBeenActive); out.textContent = log.join(); });"],
            [],
            "<html id=\"root\"><body><button id=\"b\">b</button><div id=\"out\"></div></body></html>",
            "https://example.test/page");
        session!.SettleLoadWindow();

        session.DispatchPointer(new PointerInput(PointerInputKind.Down, 20, 20) { Buttons = 1 });
        session.DispatchPointer(new PointerInput(PointerInputKind.Up, 20, 20));

        Assert.Equal("false,true,true", PageProbe.OutOf(session.SettleLoadWindow(), decode: true));
    }

    /// <summary>
    /// <c>performance.timing</c> is one <c>PerformanceTiming</c> whose moments are whole milliseconds
    /// since the epoch: the navigation's start is the time origin's millisecond, the fetch's moments are
    /// from there on, and a moment that has not happened yet -- the load event, during the page's
    /// scripts -- or that this navigation did not have reads 0.
    /// </summary>
    [Fact]
    public void PerformanceTimingIsChromiums()
    {
        Assert.Equal(
            "[object PerformanceTiming],true,connectEnd,connectStart,constructor,domComplete,domContentLoadedEventEnd," +
            "domContentLoadedEventStart,domInteractive,domLoading,domainLookupEnd,domainLookupStart,fetchStart,loadEventEnd," +
            "loadEventStart,navigationStart,redirectEnd,redirectStart,requestStart,responseEnd,responseStart,secureConnectionStart," +
            "toJSON,unloadEventEnd,unloadEventStart | 21,true | true,true,0,0,0,0 | after load: true,true,true",
            Run(
                "var t = performance.timing, names = Object.getOwnPropertyNames(PerformanceTiming.prototype)" +
                "  .filter(function (n) { return n !== 'constructor' && n !== 'toJSON'; });" +
                "var during = [t.navigationStart === Math.floor(performance.timeOrigin), t.fetchStart >= t.navigationStart && Number.isInteger(t.fetchStart)," +
                "  t.loadEventEnd, t.unloadEventStart, t.redirectStart, t.secureConnectionStart].join();" +
                "window.addEventListener('load', function () { setTimeout(function () {" +
                "  done([Object.prototype.toString.call(t), performance.timing === t, Object.getOwnPropertyNames(PerformanceTiming.prototype).sort().join()].join() +" +
                "    ' | ' + [Object.keys(t.toJSON()).length, names.every(function (n) { return n in t.toJSON(); })].join() + ' | ' + during +" +
                "    ' | after load: ' + [t.loadEventEnd >= t.loadEventStart && t.loadEventStart > 0, t.domComplete >= t.domInteractive && t.domInteractive > 0," +
                "      Number.isInteger(t.loadEventEnd)].join()); }, 0); });"));
    }

    /// <summary>
    /// <c>performance.navigation</c> is one <c>PerformanceNavigation</c>: a navigation, not redirected,
    /// with the interface's four constants. <c>performance.toJSON()</c> carries both legacy objects'.
    /// </summary>
    [Fact]
    public void PerformanceNavigationIsChromiums()
    {
        Assert.Equal(
            "[object PerformanceNavigation],0,0,0,1,2,255,1,{\"type\":0,\"redirectCount\":0},true" +
            " | TYPE_BACK_FORWARD,TYPE_NAVIGATE,TYPE_RELOAD,TYPE_RESERVED,constructor,redirectCount,toJSON,type" +
            " | timeOrigin,timing,navigation,object,0",
            Run(
                "var n = performance.navigation, json = performance.toJSON();" +
                "done([Object.prototype.toString.call(n), n.type, n.redirectCount, PerformanceNavigation.TYPE_NAVIGATE, PerformanceNavigation.TYPE_RELOAD," +
                "  PerformanceNavigation.TYPE_BACK_FORWARD, PerformanceNavigation.TYPE_RESERVED, n.TYPE_RELOAD, JSON.stringify(n.toJSON())," +
                "  performance.navigation === n].join() + ' | ' + Object.getOwnPropertyNames(PerformanceNavigation.prototype).sort().join() +" +
                "  ' | ' + [Object.keys(json).join(), typeof json.timing, json.navigation.type].join());"));
    }

    /// <summary>
    /// A page nothing opened has a <c>null</c> <c>opener</c>, an accessor of the window's own; assigning it
    /// replaces it with what was assigned.
    /// </summary>
    [Fact]
    public void OpenerIsNull()
    {
        Assert.Equal(
            "null,function,function,true,true | 5",
            Run(
                "var d = Object.getOwnPropertyDescriptor(window, 'opener'), before = [String(window.opener), typeof d.get, typeof d.set, d.enumerable, d.configurable].join();" +
                "window.opener = 5;" +
                "done(before + ' | ' + window.opener);"));
    }

    /// <summary>
    /// A frame's document has a <c>referrer</c>, empty as the page's is. Chromium's frames name their
    /// page there -- its URL, or its origin for a frame of another origin and for <c>srcdoc</c> -- but
    /// this bridge sends no <c>Referer</c> with any request, a frame's included, so there is none to name.
    /// </summary>
    [Fact]
    public void AFramesReferrerIsEmpty()
    {
        const string probe = "<script>top.postMessage(typeof document.referrer + ':' + document.referrer, '*');</script>";
        Assert.Equal(
            "string: | string:",
            Run(
                "window.addEventListener('message', function (m) { done('string:' + document.referrer + ' | ' + m.data); });" +
                "var touch = document.getElementById('f').contentWindow;",
                $"<iframe id=\"f\" srcdoc=\"{WebUtility.HtmlEncode(probe)}\"></iframe>"));
    }
}
