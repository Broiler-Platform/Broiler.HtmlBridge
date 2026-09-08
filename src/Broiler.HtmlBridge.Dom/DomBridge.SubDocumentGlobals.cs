using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Logging;

namespace Broiler.HtmlBridge;

/// <summary>
/// What a nested browsing context's own scripts declare, published on that frame's <c>window</c>.
/// <para>
/// A sub-document's scripts are evaluated in the shared JS context, so a top-level
/// <c>function foo() {}</c> or <c>var foo</c> becomes a plain global. The frame's <c>window</c> object
/// is a different object again, so <c>frames[0].window.foo</c> — how a parent page reaches into a
/// frame it controls — stayed <c>undefined</c> even though the script had run and the function
/// existed. WPT <c>css-view-transitions/transition-in-empty-iframe</c> drives its whole test that way
/// (<c>frames[0].window.startTransition()</c>), so nothing in the frame ever happened.
/// </para>
/// <para>
/// Publishing the name is only half of it. The declarations are shared-context globals, so calling one
/// from the parent would run it in the <em>parent's</em> realm: its <c>document</c> would be the parent
/// document, and a frame function that means "my document" would silently operate on the wrong one. A
/// promoted function is therefore wrapped so that invoking it re-enters its own frame's context — the
/// same <c>window</c>/<c>document</c>/<c>location</c>/<c>parent</c> swap the frame's scripts were
/// evaluated under.
/// </para>
/// <para>
/// <b>That a declaration is reachable on the global object at all is a capability, not a given.</b>
/// The diff below is <see cref="JsCapabilities.GlobalIsVariableScope"/> in action: it works because
/// under this engine the global object <em>is</em> the variable scope a top-level <c>var</c> lands in,
/// so <c>Object.getOwnPropertyNames(globalThis)</c> grows by exactly the names the frame declared. An
/// engine that keeps its script scope elsewhere would report nothing here, and the frame's window
/// would carry only what the bridge itself put on it.
/// </para>
/// </summary>
public sealed partial class DomBridge
{
    /// <summary>The own-property names of the global object right now — the "before" half of the
    /// diff that identifies what a sub-document's scripts went on to declare.</summary>
    /// <remarks>
    /// Host script, and read rather than enumerated: <c>Object.getOwnPropertyNames</c> reports the
    /// non-enumerable names too — which is most of the built-ins, and every name a
    /// <c>function</c> declaration adds — where the realm's own enumeration reports only the
    /// enumerable ones. The source is this repository's, so it is exempt from the page's content
    /// policy.
    /// </remarks>
    private List<string> GlobalOwnPropertyNames()
    {
        var names = new List<string>();
        if (_realm is not { } realm)
            return names;

        var list = realm.EvaluateHostScript("Object.getOwnPropertyNames(globalThis)", "broiler:global-names");
        if (!list.IsObject)
            return names;

        var lengthValue = realm.GetProperty(list, "length");
        if (lengthValue.IsUndefined)
            return names;

        var length = (int)lengthValue.AsNumber;
        for (var i = 0; i < length; i++)
        {
            var item = realm.GetIndex(list, (uint)i);
            if (!item.IsNullish)
                names.Add(realm.ToJsString(item));
        }

        return names;
    }

    /// <summary>The names each frame's scripts declared, waiting for that frame's window to exist.
    /// <para>The window a frame's scripts run against is not the one that survives: building the
    /// sub-document is what runs them, and it reaches back for a window before
    /// <see cref="Dom.Features.SubWindowBinding.GetOrCreate"/> has cached the real one, so the
    /// re-entrant call mints a throwaway that the outer call then replaces. Recording the names here
    /// and publishing them once the surviving window is built keeps that ordering untouched.</para>
    /// </summary>
    private readonly Dictionary<DomElement, List<string>> _pendingSubDocumentGlobals = [];

    /// <summary>Records the globals a frame's scripts have just declared — the names present now that
    /// were not present in <paramref name="namesBefore"/> — for
    /// <see cref="PublishPendingSubDocumentGlobals"/> to publish on the frame's window.</summary>
    private void RecordSubDocumentGlobals(DomElement containerElement, List<string> namesBefore)
    {
        var before = new HashSet<string>(namesBefore, StringComparer.Ordinal);
        var declared = GlobalOwnPropertyNames().Where(name => !before.Contains(name)).ToList();
        if (declared.Count > 0)
            _pendingSubDocumentGlobals[containerElement] = declared;
    }

    /// <summary>
    /// Publishes the recorded declarations on <paramref name="subWindow"/>.
    /// <para>Names the frame's window already carries are left alone, so the bridge's own window
    /// members always win over a page's same-named declaration.</para>
    /// </summary>
    private void PublishPendingSubDocumentGlobals(DomElement containerElement, JsValue subWindow)
    {
        if (_realm is not { } realm
            || !_pendingSubDocumentGlobals.Remove(containerElement, out var declaredNames))
        {
            return;
        }

        foreach (var name in declaredNames)
        {
            if (!realm.GetProperty(subWindow, name).IsUndefined)
                continue;

            try
            {
                var value = realm.GetProperty(realm.Global, name);
                if (value.IsUndefined)
                    continue;

                realm.DefineValue(
                    subWindow,
                    name,
                    value.IsFunction ? BindToFrameContext(value, subWindow, name) : value);
            }
            catch (Exception ex)
            {
                // One name that resists reading or defining must not cost the rest.
                RenderLogger.LogWarning(LogCategory.JavaScript, "DomBridge.PublishPendingSubDocumentGlobals",
                    $"Could not publish sub-document global '{name}': {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Wraps a frame's declared function so that calling it — from anywhere, including the parent
    /// page — runs it inside that frame's context, where <c>document</c> is the frame's document.
    /// </summary>
    private JsValue BindToFrameContext(JsValue declared, JsValue subWindow, string name)
    {
        var realm = Realm;

        return realm.NewMethod(name, (in call) =>
        {
            // JsCall is a ref struct and cannot be captured, so the values are copied out first.
            var forwarded = new JsValue[call.Length];
            for (var i = 0; i < forwarded.Length; i++)
                forwarded[i] = call[i];

            var result = JsValue.Undefined;
            RunWithWindowContext(subWindow, () =>
            {
                result = realm.Invoke(declared, subWindow, forwarded);
            });
            return result;
        }, 0);
    }
}
