using Broiler.Dom;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;
using Broiler.JavaScript.BuiltIns.Function;
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
/// </summary>
public sealed partial class DomBridge
{
    /// <summary>The own-property names of the global object right now — the "before" half of the
    /// diff that identifies what a sub-document's scripts went on to declare.</summary>
    private List<string> GlobalOwnPropertyNames()
    {
        var names = new List<string>();
        if (_jsContext?.Eval("Object.getOwnPropertyNames(globalThis)") is not JSObject list)
            return names;

        var lengthValue = list[(KeyString)"length"];
        if (lengthValue is null || lengthValue.IsUndefined)
            return names;

        var length = (int)lengthValue.DoubleValue;
        for (var i = 0; i < length; i++)
        {
            if (list[(uint)i] is { } item && !item.IsUndefined && !item.IsNull)
                names.Add(item.ToString());
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
    private void PublishPendingSubDocumentGlobals(DomElement containerElement, JSObject subWindow)
    {
        if (_jsContext is not { } context
            || !_pendingSubDocumentGlobals.Remove(containerElement, out var declaredNames))
        {
            return;
        }

        foreach (var name in declaredNames)
        {
            if (subWindow[(KeyString)name] is { IsUndefined: false })
                continue;

            try
            {
                var value = context[name];
                if (value is null || value.IsUndefined)
                    continue;

                subWindow.FastAddValue(name,
                    value is JSFunction declared ? BindToFrameContext(declared, subWindow, name) : value,
                    JSPropertyAttributes.EnumerableConfigurableValue);
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
    private JSFunction BindToFrameContext(JSFunction declared, JSObject subWindow, string name)
    {
        return new DomFunction((in Arguments arguments) =>
        {
            // Arguments is a ref struct and cannot be captured, so the values are copied out first.
            var forwarded = new JSValue[arguments.Length];
            for (var i = 0; i < arguments.Length; i++)
                forwarded[i] = arguments[i];

            JSValue result = JSUndefined.Value;
            RunWithWindowContext(subWindow, () =>
            {
                result = declared.InvokeFunction(new Arguments(subWindow, forwarded));
            });
            return result;
        }, name, 0);
    }
}
