using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Logging;

namespace Broiler.HtmlBridge;

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
