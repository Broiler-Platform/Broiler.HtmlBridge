using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.Storage;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The <c>Element.classList</c> / <c>DOMTokenList</c> feature binding (HtmlBridge
/// complexity-reduction roadmap Phase 3, P3.6). It is pure logic over the element's <c>class</c>
/// attribute via the canonical <see cref="DomTokenList"/>, so it needs no host contract at all: the
/// only bridge coupling is an injected <paramref name="onClassChanged"/> callback the mutating
/// operations invoke so the bridge can invalidate the element's style scope. This replaces the
/// bridge's <c>BuildClassListObject</c> plus its five scattered <c>JsUtilities…025…Core</c>
/// callbacks.
/// </summary>
internal static class ClassListBinding
{
    /// <summary>
    /// Builds the JS <c>DOMTokenList</c> exposed as <c>element.classList</c>. Mutating operations
    /// invoke <paramref name="onClassChanged"/> (typically the bridge's style-scope invalidation).
    /// </summary>
    internal static JSObject Build(DomElement element, Action<DomElement>? onClassChanged)
    {
        var classList = new JSObject();

        classList.FastAddValue("contains",
            new DomFunction((in a) => Contains(element, in a), "contains", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        classList.FastAddValue("add",
            new DomFunction((in a) => Add(element, onClassChanged, in a), "add"),
            JSPropertyAttributes.EnumerableConfigurableValue);

        classList.FastAddValue("remove",
            new DomFunction((in a) => Remove(element, onClassChanged, in a), "remove"),
            JSPropertyAttributes.EnumerableConfigurableValue);

        classList.FastAddValue("toggle",
            new DomFunction((in a) => Toggle(element, onClassChanged, in a), "toggle", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        classList.FastAddValue("replace",
            new DomFunction((in a) => Replace(element, onClassChanged, in a), "replace", 2),
            JSPropertyAttributes.EnumerableConfigurableValue);

        return classList;
    }

    private static JSValue Contains(DomElement element, in Arguments a)
    {
        if (a.Length == 0)
            return JSBoolean.False;
        return new DomTokenList(element, "class").Contains(a[0].ToString())
            ? JSBoolean.True
            : JSBoolean.False;
    }

    private static JSValue Add(DomElement element, Action<DomElement>? onClassChanged, in Arguments a)
    {
        var tokens = new List<string>();
        for (var i = 0; i < a.Length; i++)
        {
            var cls = a[i].ToString();
            if (!string.IsNullOrEmpty(cls))
                tokens.Add(cls);
        }

        new DomTokenList(element, "class").Add([.. tokens]);
        onClassChanged?.Invoke(element);
        return JSUndefined.Value;
    }

    private static JSValue Remove(DomElement element, Action<DomElement>? onClassChanged, in Arguments a)
    {
        var tokens = new List<string>();
        for (var i = 0; i < a.Length; i++)
        {
            var cls = a[i].ToString();
            if (!string.IsNullOrEmpty(cls))
                tokens.Add(cls);
        }

        new DomTokenList(element, "class").Remove([.. tokens]);
        onClassChanged?.Invoke(element);
        return JSUndefined.Value;
    }

    private static JSValue Toggle(DomElement element, Action<DomElement>? onClassChanged, in Arguments a)
    {
        if (a.Length == 0)
            return JSBoolean.False;
        var cls = a[0].ToString();
        bool? force = a.Length >= 2 && a[1] is not JSUndefined ? a[1].BooleanValue : null;
        var present = new DomTokenList(element, "class").Toggle(cls, force);
        onClassChanged?.Invoke(element);
        return present ? JSBoolean.True : JSBoolean.False;
    }

    private static JSValue Replace(DomElement element, Action<DomElement>? onClassChanged, in Arguments a)
    {
        if (a.Length < 2)
            return JSBoolean.False;
        var replaced = new DomTokenList(element, "class").Replace(a[0].ToString(), a[1].ToString());
        if (replaced)
            onClassChanged?.Invoke(element);
        return replaced ? JSBoolean.True : JSBoolean.False;
    }
}
