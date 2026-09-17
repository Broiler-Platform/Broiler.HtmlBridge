using Broiler.Dom;
using Broiler.HtmlBridge.Core.Diagnostics;
using Broiler.HtmlBridge.Dom;
using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Logging;
using Broiler.HtmlBridge.Scripting;

namespace Broiler.HtmlBridge;

/// <summary>
/// The static members of <c>DomBridge</c> that need no bridge instance: constants, shared state and helpers,
/// in their own assembly below Broiler.HtmlBridge.Dom. The partial files here group them by topic, mostly under the
/// names of the <c>DomBridge</c> files they came from. The few helpers that need a bridge, a feature binding or the JavaScript engine are
/// <c>DomBridgeHostUtils</c>, which stays in Dom.
/// </summary>
public static partial class DomBridgeUtils
{
    /// <summary>
    /// Safety cap for draining bridge-backed microtask/timer work so promise/timer
    /// chains can settle without risking an infinite loop in test and capture paths.
    /// </summary>
    public const int AsyncDrainIterationLimit = 1000;

    /// <summary>
    /// How far onto the virtual clock a drain follows scheduled work (ms from document start); see
    /// <see cref="DomBridgeRuntimeLimits.AsyncDrainVirtualTimeBudgetMs"/>.
    /// </summary>
    public const double AsyncDrainVirtualTimeBudgetMs = DomBridgeRuntimeLimits.AsyncDrainVirtualTimeBudgetMs;

    internal static readonly string[] InlineEventNames = ["click", "load", "change", "input", "submit", "mousedown",
        "mouseup", "mouseover", "mouseout", "keydown", "keyup", "keypress", "focus", "blur", "error", "scroll",
        "scrollend"];

    /// <summary>The viewport a bridge assumes when its host does not say otherwise.</summary>
    public const int DefaultViewportWidth = 1024;

    /// <inheritdoc cref="DefaultViewportWidth"/>
    public const int DefaultViewportHeight = 768;

    // -----------------------------------------------------------------
    // RF-BRIDGE-1c Phase E2: child-node access over canonical ChildNodes,
    // replacing the facade Broiler.Dom.DomElement.Children (LegacyChildList, since removed).
    // Phase F has since made text and comment nodes canonical DomText/DomComment children
    // (CreateBridgeTextNode), so ChildElements is an OfType filter that skips them, ChildAt
    // answers a DomNode, and callers that need text or comment children walk ChildNodes
    // with IsText/IsComment checks.
    // -----------------------------------------------------------------

    /// <summary>The element's <see cref="DomElement"/> children. RF-BRIDGE-1c Phase F (F3c part 2c):
    /// narrowed from <c>Cast</c> to <c>OfType&lt;Broiler.Dom.DomElement&gt;()</c> so it skips canonical
    /// <c>DomText</c>/<c>DomComment</c> children, which the bridge creates today (see
    /// <c>DomBridge.CreateBridgeTextNode</c>). Callers that need text/comment children walk raw
    /// <c>ChildNodes</c> instead.</summary>
    internal static IEnumerable<DomElement> ChildElements(DomNode element) =>
        element.ChildNodes.OfType<DomElement>();

    /// <summary>The child node at <paramref name="index"/> (old <c>Children[index]</c>). RF-BRIDGE-1c
    /// Phase F (F3c part 2c): returns canonical <see cref="DomNode"/> — a child may be a
    /// <c>DomText</c>/<c>DomComment</c>. Element-only callers narrow with <c>as Broiler.Dom.DomElement</c>
    /// or <c>is Broiler.Dom.DomElement</c>, since not every child is an element.</summary>
    internal static DomNode ChildAt(DomNode element, int index) => element.ChildNodes[index];

    /// <summary>The child node at <paramref name="index"/>, supporting from-end indices like <c>^1</c>
    /// (old <c>Children[^1]</c>); canonical <c>ChildNodes</c> is an <c>IReadOnlyList</c> with no
    /// from-end indexer.</summary>
    internal static DomNode ChildAt(DomNode element, Index index) =>
        element.ChildNodes[index.GetOffset(element.ChildNodes.Count)];

    /// <summary>Index of <paramref name="child"/> among the element's children, or -1
    /// (old <c>Children.IndexOf</c>, reference equality). Phase 4 item 4/5: canonical
    /// <c>Broiler.Dom.DomNodeCollectionExtensions.IndexOfReference</c> is the byte-identical scan, but the
    /// reference-equality child-index scan is the canonical <c>DomNodeCollectionExtensions.IndexOfReference</c>
    /// (P4.17 reuse), which `patches/0002` made public and which is now pinned — so the former manual loop
    /// delegates to it (byte-identical).</summary>
    internal static int ChildIndexOf(DomNode element, DomNode child) => element.ChildNodes.IndexOfReference(child);

    // RF-BRIDGE-1c Phase F (F3c part 2b): the child-mutation helpers take a DomNode parent so
    // range-extract code (whose ancestor-chain clones are DomNode-typed) can reparent without
    // casts. At runtime the parent is always an element; canonical AppendChild/InsertBefore/
    // RemoveChild enforce nothing text-specific, so this is a safe widen.

    /// <summary>Old <c>Children.Insert(index, child)</c>.</summary>
    internal static void InsertChildAt(DomNode parent, int index, DomNode child)
    {
        var reference = index < parent.ChildNodes.Count ? parent.ChildNodes[index] : null;
        parent.InsertBefore(child, reference);
    }

    /// <summary>Old <c>Children.Remove(child)</c> — removes only if actually a child; returns success.</summary>
    internal static bool RemoveChildFrom(DomNode parent, DomNode child)
    {
        if (!ReferenceEquals(child.ParentNode, parent))
            return false;

        parent.RemoveChild(child);
        return true;
    }

    /// <summary>Old raw <c>Children.RemoveAt(index)</c>, now canonical <c>RemoveChild</c>, which publishes
    /// its own child-list mutation record.</summary>
    internal static void RemoveNthChild(DomNode parent, int index) => parent.RemoveChild(parent.ChildNodes[index]);

    /// <summary>Old <c>Children.Clear()</c>.</summary>
    internal static void ClearChildren(DomNode parent)
    {
        foreach (var child in parent.ChildNodes.ToArray())
            parent.RemoveChild(child);
    }

    /// <summary>Whether <paramref name="node"/> is a text node (RF-BRIDGE-1c Phase D: replaces
    /// the facade <c>IsText(Broiler.Dom.DomElement)</c>). NodeType-based; construction has flipped,
    /// so a text node is a canonical <c>DomText</c> (<c>DomBridge.CreateBridgeTextNode</c>). (This said
    /// it held for facade text nodes, and for <c>DomText</c> once construction flipped.)</summary>
    internal static bool IsText(DomNode node) => node.NodeType == DomNodeType.Text;

    /// <summary>Whether <paramref name="node"/> is a comment node (RF-BRIDGE-1c Phase F).
    /// NodeType-based — the replacement for the many <c>TagName == "#comment"</c> checks, since a
    /// canonical <c>DomComment</c> has no <c>TagName</c>; construction has flipped, so every comment
    /// is one (<c>DomBridge.CreateBridgeCommentNode</c>). (This said it held for facade comment nodes,
    /// and for <c>DomComment</c> once construction flipped.)</summary>
    internal static bool IsComment(DomNode node) => node.NodeType == DomNodeType.Comment;

    /// <summary>Reads a text/comment node's character data (RF-BRIDGE-1c Phase F): a canonical
    /// <c>DomText</c>/<c>DomComment</c>'s <c>Data</c>, otherwise <c>NodeValue</c>, which no other node
    /// kind overrides, so <c>""</c> (never null). (This also named facade text/comment nodes that
    /// exposed it as <c>TextContent</c>, and a text cutover both models funnelled through.)</summary>
    internal static string BridgeText(DomNode node) => node switch
    {
        DomCharacterData characterData => characterData.Data,
        _ => node.NodeValue ?? string.Empty,
    };

    /// <summary>Writes a text/comment node's character data (see <see cref="BridgeText"/>).</summary>
    internal static void SetBridgeText(DomNode node, string value)
    {
        if (node is DomCharacterData characterData)
            characterData.Data = value;
    }

    /// <summary>The element's parent as a <see cref="DomElement"/> (RF-BRIDGE-1c Phase E:
    /// replaces the facade <c>ParentEl(Broiler.Dom.DomElement)</c> getter — <c>ParentNode as Broiler.Dom.DomElement</c>).
    /// A node's parent is always an element, so this is stable when text/comment nodes become
    /// canonical <c>DomText</c>/<c>DomComment</c> in Phase D.</summary>
    internal static DomElement? ParentEl(DomNode node) => node.ParentNode as DomElement;

    /// <summary>Reparents <paramref name="child"/> under <paramref name="parent"/> (RF-BRIDGE-1c
    /// Phase E: replaces the facade <c>ParentEl(Broiler.Dom.DomElement)</c> setter). A null parent detaches;
    /// otherwise the child is appended if not already there — matching the old setter exactly.
    /// RF-BRIDGE-1c Phase F (F3c part 2b): the parent widened to <c>DomNode?</c> so range-extract
    /// code can pass DomNode-typed ancestor-chain clones (always elements at runtime).</summary>
    internal static void SetParent(DomNode child, DomNode? parent)
    {
        if (parent is null)
            child.Remove();
        else if (!ReferenceEquals(child.ParentNode, parent))
            parent.AppendChild(child);
    }
}

public static partial class DomBridgeUtils
{
    /// <summary>
    /// The realm options a policy maps to: guest evaluation is allowed exactly when
    /// <see cref="ContentSecurityPolicy.AllowsEval"/> says so, and allowed when there is no policy.
    /// </summary>
    /// <remarks>
    /// One mapping for every path that adopts a context the host built: <c>RegisterDocumentCore</c>, with
    /// the bridge's policy, and <c>ScriptEngine</c>'s two document-free entry points, with the host's.
    /// </remarks>
    internal static JsRealmOptions RealmOptionsFor(ContentSecurityPolicy? policy) =>
        new() { AllowGuestEval = policy?.AllowsEval ?? true };
}

public static partial class DomBridgeUtils
{
    /// <summary>
    /// A window handle, or <see cref="JsValue.Null"/> when the manager answered "no window".
    /// </summary>
    /// <remarks>
    /// <b>The CLR null these forms used to answer was the same answer spelled in a type only the
    /// middle of this path used.</b> The manager speaks JSEAL and so does <c>IMessagingHost</c>;
    /// these two delegators converted a handle to an engine object on the way out and the host
    /// converted it straight back, which named one window either way. What is kept is the
    /// distinction that conversion carried: a non-object answer becomes <c>null</c> for the page,
    /// not <see cref="JsValue.Missing"/>, because "no window" is a value a page reads.
    /// </remarks>
    internal static JsValue WindowOrNull(JsValue window) => window.IsObject ? window : JsValue.Null;
}

public static partial class DomBridgeUtils
{
    /// <summary>
    /// Copies every own property of <c>window</c> that the global object does not already have
    /// onto the global, preserving each property's descriptor.
    /// <para>
    /// In a browser <c>window</c> <em>is</em> the global object, so <c>getComputedStyle(el)</c>,
    /// <c>location.href</c>, <c>innerWidth</c> and <c>scrollTo(…)</c> are all valid unqualified.
    /// Here the two are distinct objects, so an unqualified reference to a member that lived only
    /// on <c>window</c> raised a <c>ReferenceError</c> — which does not merely skip that one
    /// statement, it aborts the whole script, taking every later statement and every listener the
    /// script would have registered with it. Unqualified spellings are idiomatic in the WPT
    /// corpus, so this silently emptied entire test pages.
    /// </para>
    /// <para>
    /// The mirror list used to be maintained by hand, one global assignment at a time (see the timer
    /// globals in RegisterWindowGlobals), and had drifted: <c>localStorage</c>,
    /// <c>matchMedia</c>, <c>location</c>, <c>alert</c>, <c>getComputedStyle</c>, <c>self</c>,
    /// <c>innerWidth</c>/<c>innerHeight</c>, <c>outerWidth</c>/<c>outerHeight</c>,
    /// <c>scrollX</c>/<c>scrollY</c>, <c>pageXOffset</c>/<c>pageYOffset</c> and
    /// <c>scroll</c>/<c>scrollTo</c>/<c>scrollBy</c> were all missing. Sweeping instead of listing
    /// keeps the two in step as window members are added.
    /// </para>
    /// <para>
    /// Runs last, after every Register* pass, so it sees the fully-built window. It copies
    /// descriptors rather than values, so accessor-backed members (<c>innerWidth</c> and friends)
    /// stay live getters rather than freezing to a snapshot, and value members share the identical
    /// object — a listener added through the global <c>addEventListener</c> is therefore removable
    /// through <c>window.removeEventListener</c>. Properties the global already owns are left
    /// alone, so engine builtins and the explicit aliases above always win.
    /// </para>
    /// <para>
    /// This pass covers the members <em>the bridge</em> installs. A member a <em>page script</em>
    /// adds later — the shape every WPT support library has, <c>window.foo = …</c> in one
    /// <c>&lt;script&gt;</c> and an unqualified <c>foo(…)</c> in the next — appears after it has
    /// run, so a host that evaluates scripts one at a time must call
    /// <c>DomBridge.SyncWindowMembersOntoGlobal</c> between them.
    /// </para>
    /// </summary>
    internal static void MirrorWindowMembersOntoGlobal(IJsRealm realm, JsValue window)
    {
        // The bridge now makes `window` the global object (see RegisterDocumentCore), so there is
        // nothing to copy and no gap to close — the sweep would define every own property of the
        // global onto itself. Returning here keeps that off the per-script path a host runs
        // (WptTestRunner calls SyncWindowMembersOntoGlobal after every script) instead of paying for
        // an Object.getOwnPropertyNames walk of the whole global that skips all of its own results.
        // The sweep is kept rather than deleted because it is still correct for any realm where the
        // two are genuinely distinct objects — which is exactly what a provider that does not declare
        // JsCapabilities.GlobalIsVariableScope may present, so the comparison is on the handles
        // rather than on an assumption.
        if (realm.Global == window)
            return;

        realm.SetProperty(realm.Global, "__broilerWindowForGlobalMirror", window);
        try
        {
            realm.EvaluateHostScript(@"
(function() {
  var w = __broilerWindowForGlobalMirror;
  var g = globalThis;
  var names = Object.getOwnPropertyNames(w);
  for (var i = 0; i < names.length; i++) {
    var name = names[i];
    if (name in g) continue;
    var descriptor = Object.getOwnPropertyDescriptor(w, name);
    if (!descriptor) continue;
    // A member that resists definition on the global (a frozen builtin slot, say) is
    // skipped rather than aborting the sweep for every member after it.
    try { Object.defineProperty(g, name, descriptor); } catch (e) {}
  }
})();", "bridge:window-global-mirror");
        }
        catch (Exception ex)
        {
            RenderLogger.LogError(LogCategory.JavaScript, "DomBridge.MirrorWindowMembersOntoGlobal",
                $"Error mirroring window members onto the global object: {ex.Message}", ex);
        }
        finally
        {
            realm.EvaluateHostScript(
                "delete globalThis.__broilerWindowForGlobalMirror;", "bridge:window-global-mirror-cleanup");
        }
    }
}

public static partial class DomBridgeUtils
{
    internal static JsValue StoreHistoryState(JsValue history, in JsCall call)
    {
        // `history.state` is re-defined rather than assigned, which is what the engine-typed
        // installer did: the slot keeps the attributes it was created with, and an argument the page
        // did not pass reads as null rather than undefined.
        call.Realm.DefineValue(history, "state", call.Length > 0 ? call[0] : JsValue.Null);

        return JsValue.Undefined;
    }
}

public static partial class DomBridgeUtils
{
    /// <summary>
    /// Calls one registered listener -- a function, or an object with a <c>handleEvent</c> -- and
    /// swallows what it throws into a warning, because a listener that fails must not abort the
    /// dispatch of the ones after it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The listener is a handle and the call goes through the realm.</b> Five call sites over four
    /// firing paths reach this: element and document dispatch (<c>Features/EventDispatchBinding.cs</c>),
    /// window dispatch (<c>DomBridge/Lifecycle.cs</c>), form submit (<c>Features/FormSubmitBinding.cs</c>)
    /// and messaging (<c>Features/MessagingBinding.EventTarget.cs</c>, once for a registration and once for an
    /// <c>on…</c> handler). Four hand over an <c>EventListenerRegistration</c>'s listener field, which
    /// holds a <see cref="JsValue"/> now, and the fifth reads its handler through the realm. Nothing on
    /// any of those paths converts a listener or an event, and nothing here names an engine type.
    /// </para>
    /// <para>
    /// <b>The receivers are the ones the engine-typed calls passed, including the odd one.</b> A function
    /// listener is its own <c>this</c>, as the engine argument frame built from the function made it --
    /// where DOM's inner invoke passes the event's <c>currentTarget</c> -- and an object's
    /// <c>handleEvent</c> is called with the object. <c>Features/EventDispatchBinding.cs</c> already fires
    /// the inline <c>on*</c> handler through the realm with itself as receiver; this is that shape.
    /// </para>
    /// <para>
    /// <b>Three things differ from the direct engine call, and all three were read rather than
    /// assumed.</b> <see cref="IJsCalls.Invoke"/> takes the provider's realm scope for the call, and so
    /// does the <c>handleEvent</c> lookup, which is <see cref="IJsMembers.GetProperty"/> -- the same
    /// indexer, so a getter for it still runs once. For the realm this bridge adopts, that scope makes the
    /// realm's context the engine's current context and restores the previous one after -- and nothing
    /// else, because the provider installs a job pump only for a context it created; a listener already
    /// running under that context sees nothing change. An exception the listener throws reaches the catch
    /// below as the provider's <see cref="JsEngineException"/> rather than the engine's own, constructed
    /// from the same message, so the warning reads the same. And the event is no longer unwrapped ahead
    /// of the turn, where a handle carrying no object used to fail out to the dispatch that passed it;
    /// none can arrive, because every <c>dispatchEvent</c> a page can call refuses a non-object, and every
    /// event the bridge dispatches itself is an object it minted or tested as one.
    /// </para>
    /// </remarks>
    internal static void InvokeEventListener(
        IJsRealm realm, JsValue listener, JsValue evt, string logContext)
    {
        // Every DOM listener the page runs passes through here, which makes this the one place a
        // listener turn can be bracketed. Inactive unless a run asked for it; see JsEntryTrace.
        using var turn = JsEntryTrace.Enter(JsEntryKind.Event, EventTurnLabel(realm, evt, logContext));

        try
        {
            if (listener.IsFunction)
            {
                realm.Invoke(listener, listener, [evt]);
                return;
            }

            if (!listener.IsObject)
                return;

            var handleEvent = realm.GetProperty(listener, "handleEvent");
            if (handleEvent.IsFunction)
                realm.Invoke(handleEvent, listener, [evt]);
        }
        catch (Exception ex)
        {
            RenderLogger.LogWarning(LogCategory.JavaScript, logContext, $"Event listener error: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Names a listener turn as <c>type@context</c> for the trace. The event type is what makes a line
    /// actionable — "20 s idle before click@document" says which interaction ended the idle, where the
    /// call site alone says only that some listener ran.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reads the property only while the trace is active, and answers with the call site alone if the
    /// read throws: <c>type</c> is a data property on every event this bridge constructs, but a page
    /// may dispatch an object of its own through <c>dispatchEvent</c>, and a diagnostic does not get to
    /// turn that into a failure.
    /// </para>
    /// <para>
    /// <b>Through the realm, and it answers what the engine-typed read answered.</b> The realm's
    /// property read is the engine's own indexer on the same object, so a page-defined <c>type</c>
    /// accessor still runs, and the three "no type" answers the former test named — never
    /// installed, <c>null</c>, <c>undefined</c> — come back as exactly the three kinds
    /// <see cref="JsValue.IsNullish"/> tests. <see cref="IJsValues.ToJsString"/> rather than the
    /// handle's own rendering, because the label used to interpolate the engine's value, and that is
    /// the engine's <c>ToString</c>: the ECMAScript coercion, which may run a <c>toString</c> the page
    /// wrote. The handle would render an object as <c>[object]</c> and run nothing.
    /// </para>
    /// <para>
    /// <b>The one difference is the realm's scope</b>, which each of the two calls takes: page code
    /// either of them reaches runs with this document's context installed as the engine's current
    /// one, rather than with whatever the thread was carrying. That needs the trace to be active and a
    /// <c>type</c> the page supplied as an accessor or an object — the events this bridge builds carry
    /// a string, and reading one runs nothing — and on the window and generic event-target paths
    /// that same accessor and that same coercion have already run inside the realm's scope at the top
    /// of the same dispatch, to find the listeners.
    /// </para>
    /// </remarks>
    private static string EventTurnLabel(IJsRealm realm, JsValue evt, string logContext)
    {
        if (!JsEntryTrace.IsActive)
            return logContext;

        try
        {
            var type = realm.GetProperty(evt, "type");
            return type.IsNullish ? logContext : $"{realm.ToJsString(type)}@{logContext}";
        }
        catch (Exception)
        {
            return logContext;
        }
    }

    /// <summary>
    /// Whether <paramref name="element"/> is SVG content — the <c>&lt;svg&gt;</c> element itself or
    /// anything inside one. Decided by walking ancestors rather than reading a namespace, so it
    /// holds for a fragment the HTML parser built as well as one script created with
    /// <c>createElementNS</c>.
    /// </summary>
    internal static bool IsInSvgContent(DomElement element)
    {
        for (var node = element; node != null; node = ParentEl(node))
        {
            if (string.Equals(node.TagName, "svg", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
