using Broiler.HtmlBridge.Scripting;
using Broiler.JavaScript.Engine;
using System;
using System.Collections.Generic;

namespace Broiler.HtmlBridge.Dom;

/// <summary>
/// Narrow runtime surface required by script execution and interactive sessions.
/// </summary>
public interface IDomBridgeRuntime
{
    ContentSecurityPolicy? Csp { get; set; }

    Action? TaskCheckpointCallback { get; set; }

    IReadOnlyList<Broiler.Dom.DomElement> Elements { get; }

    int CurrentScriptIndex { get; set; }

    bool HasPendingTimers { get; }

    /// <summary>
    /// Whether queued timer/animation-frame work is due at or before <paramref name="virtualHorizonMs"/>
    /// on the event loop's virtual clock (ms from document start). Draining asks this rather than
    /// <see cref="HasPendingTimers"/>, which never goes false on a page holding a <c>setInterval</c>.
    /// </summary>
    bool HasPendingTimersDueBy(double virtualHorizonMs);

    /// <summary>
    /// Takes the cross-document navigation the page asked for, clearing it, or returns <c>null</c>
    /// if it asked for none.
    /// <para>
    /// Call it after script execution has settled: a navigation is a request to leave a document
    /// that is still running, and acting on it mid-script would tear down the context underneath
    /// the code that made the request. The last request wins, as it does in a browser, where a
    /// second assignment supersedes the navigation the first one started.
    /// </para>
    /// <para>
    /// <b>Taking, not reading.</b> A host asks this at more than one moment — once while loading and
    /// again while the loaded page runs on — and a request that stayed put after the first ask was
    /// served twice: the load decided not to follow it, and the second ask performed it anyway,
    /// several seconds later and with none of the first decision's budgets. Consuming it means the
    /// later ask sees only what the page asked for after the earlier one, which is the only thing
    /// it should act on. A page that still wants to leave asks again, and that is a new decision.
    /// </para>
    /// </summary>
    NavigationRequest? TakePendingNavigation();

    void Attach(JSContext context, string html);

    void Attach(JSContext context, string html, string url);

    void FireWindowLoadEvent();

    bool FlushTimerStep();

    void FlushTimers();

    string SerializeToHtml();

    Broiler.Dom.DomDocument GetRenderDocument();
}

/// <summary>
/// Creates bridge runtime instances without coupling script execution to a concrete bridge class.
/// </summary>
public interface IDomBridgeRuntimeFactory
{
    IDomBridgeRuntime Create();
}

public static class DomBridgeRuntimeLimits
{
    public const int AsyncDrainIterationLimit = 1000;

    /// <summary>
    /// How far onto the event loop's virtual clock a drain will follow scheduled work, in ms from
    /// document start.
    /// </summary>
    /// <remarks>
    /// A capture is a moment shortly after load, not a session. Timers scheduled past this horizon —
    /// a polling interval's later ticks, a delayed refresh — belong to a page that kept running, and
    /// following them would both never terminate and simulate page time the capture does not
    /// represent. The iteration limit remains the backstop for work that is due immediately and keeps
    /// regenerating, which no time horizon can bound because it never moves the clock.
    /// </remarks>
    public const double AsyncDrainVirtualTimeBudgetMs = 5000;
}
