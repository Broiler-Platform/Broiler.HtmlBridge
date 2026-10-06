using System.Runtime.CompilerServices;
using Broiler.Dom;
using Broiler.HtmlBridge.Logging;
using Broiler.JSeal;

namespace Broiler.HtmlBridge;

/// <summary>
/// A frame's document loading: its <c>readyState</c>, and the <c>readystatechange</c>,
/// <c>DOMContentLoaded</c>, <c>load</c> and <c>pageshow</c> that tell its scripts it has.
/// </summary>
/// <remarks>
/// <para>
/// <b>A frame's document never loaded.</b> It had no <c>readyState</c>, and nothing fired any of these
/// for it, so a frame's script that waited for its document or its window to load waited for ever --
/// or, listening without naming a window, heard the page's. The page's own document goes through
/// them in <see cref="FireWindowLoadEvent"/>.
/// </para>
/// <para>
/// <b>In Chromium's order, measured.</b> The frame's scripts run with the document <c>loading</c>. Then
/// it goes <c>interactive</c>, gets <c>DOMContentLoaded</c> (which bubbles to the frame's window), goes
/// <c>complete</c>, and the frame's window gets <c>load</c>, whose target is the document, and its
/// <c>onload</c>. The page's frame element gets <c>load</c> next (<see cref="FireSubDocumentOnload"/>),
/// and the frame's window gets <c>pageshow</c> last. Each runs as the frame's script.
/// </para>
/// <para>
/// The frame's document is built and its scripts run at once, when the page first reaches the frame,
/// so it loads then; a browser loads it in a later task. A frame's module scripts run as jobs after
/// that, so they come after its <c>DOMContentLoaded</c> rather than before it.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    /// <summary>Where a frame's document is in its load.</summary>
    private sealed class FrameDocumentLoad
    {
        /// <summary><c>document.readyState</c>: <c>loading</c>, <c>interactive</c> or <c>complete</c>.</summary>
        public string ReadyState = "loading";

        /// <summary>Whether the frame's window is owed its <c>pageshow</c>, which follows the frame element's <c>load</c>.</summary>
        public bool PageShowPending;
    }

    /// <summary>Each frame document's load, weakly keyed so a document a frame navigated away from goes.</summary>
    private readonly ConditionalWeakTable<DomDocument, FrameDocumentLoad> _frameDocumentLoads = new();

    /// <summary><c>document.readyState</c> of <paramref name="document"/>: the page's, or a frame's.</summary>
    internal string ReadyStateOf(DomDocument document)
    {
        if (ReferenceEquals(document, _document))
            return _documentReadyState;

        // A document no frame loaded -- one a script made -- has nothing left to load.
        return _frameDocumentLoads.TryGetValue(document, out var load) ? load.ReadyState : "complete";
    }

    /// <summary>Starts a frame document's load, before its scripts run; a document already started keeps its state.</summary>
    private void BeginFrameDocumentLoad(DomDocument document) =>
        _frameDocumentLoads.GetValue(document, static _ => new FrameDocumentLoad());

    /// <summary>
    /// Finishes a frame document's load once its scripts have run: <c>interactive</c> and
    /// <c>DOMContentLoaded</c>, then <c>complete</c> and the frame window's <c>load</c>.
    /// </summary>
    private void CompleteFrameDocumentLoad(DomElement container, DomDocument document)
    {
        if (_realm is null || !_frameDocumentLoads.TryGetValue(document, out var load) || load.ReadyState != "loading")
            return;

        // A frame whose window nothing has built ran no script, so nothing listens to it.
        if (!_browsingContexts.TryGetSubWindow(container, out var window) || !window.IsObject ||
            !_browsingContexts.TryGetSubDocument(container, out var documentObject))
        {
            load.ReadyState = "complete";
            return;
        }

        RunWithWindowContext(window, () =>
        {
            SetFrameReadyState(document, load, "interactive");
            DispatchFrameDocumentEvent(document, FrameLoadEvent("DOMContentLoaded", bubbles: true));
            SetFrameReadyState(document, load, "complete");
            DispatchFrameWindowLoadEvent(window, FrameLoadEvent("load", bubbles: false), documentObject);
        });

        load.PageShowPending = true;
    }

    /// <summary>The frame window's <c>pageshow</c>, after the page's frame element has had its <c>load</c>.</summary>
    private void FireFramePageShow(DomElement container)
    {
        if (_realm is null ||
            GetContentDocument(container) is not { } document ||
            !_frameDocumentLoads.TryGetValue(document, out var load) || !load.PageShowPending ||
            !_browsingContexts.TryGetSubWindow(container, out var window) || !window.IsObject ||
            !_browsingContexts.TryGetSubDocument(container, out var documentObject))
        {
            return;
        }

        load.PageShowPending = false;
        var evt = FrameLoadEvent("pageshow", bubbles: false, "PageTransitionEvent");
        Realm.DefineValue(evt, "persisted", JsValue.False);
        RunWithWindowContext(window, () => DispatchFrameWindowLoadEvent(window, evt, documentObject));
    }

    private void SetFrameReadyState(DomDocument document, FrameDocumentLoad load, string state)
    {
        load.ReadyState = state;
        DispatchFrameDocumentEvent(document, FrameLoadEvent("readystatechange", bubbles: false));
    }

    private void DispatchFrameDocumentEvent(DomDocument document, JsValue evt)
    {
        try
        {
            _eventDispatch.DispatchEventOnElement(document, evt);
        }
        catch (Exception ex)
        {
            RenderLogger.LogWarning(LogCategory.JavaScript, "DomBridge.CompleteFrameDocumentLoad",
                $"Frame document event error: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// <c>load</c> or <c>pageshow</c> at a frame's window, with its document as the target: HTML fires
    /// both at the window with the legacy target override, so a listener's <c>e.target</c> is the
    /// document, and they reach no listener of the document.
    /// </summary>
    private void DispatchFrameWindowLoadEvent(JsValue window, JsValue evt, JsValue documentObject)
    {
        try
        {
            _eventDispatch.DispatchEventOnWindow(window, evt, documentObject);
        }
        catch (Exception ex)
        {
            RenderLogger.LogWarning(LogCategory.JavaScript, "DomBridge.CompleteFrameDocumentLoad",
                $"Frame window event error: {ex.Message}", ex);
        }
    }

    /// <summary>A trusted event of the load sequence: not cancelable.</summary>
    private JsValue FrameLoadEvent(string type, bool bubbles, string interfaceName = "Event") =>
        NewTrustedEvent(Realm, type, bubbles, cancelable: false, composed: false, InterfacePrototype(Realm, interfaceName));
}
