using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

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
/// <remarks>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>), so nothing here names an engine
/// type; the realm arrives on the call frame for each operation and as a parameter when the list is
/// built. The <see cref="DomElement"/> and the callback are still captured by the operation closures
/// exactly as before — the token-list logic never was engine-coupled.
/// </remarks>
internal static class ClassListBinding
{
    /// <summary>
    /// Builds the JS <c>DOMTokenList</c> exposed as <c>element.classList</c>. Mutating operations
    /// invoke <paramref name="onClassChanged"/> (typically the bridge's style-scope invalidation).
    /// </summary>
    internal static JsValue Build(IJsRealm realm, DomElement element, Action<DomElement>? onClassChanged)
    {
        var classList = realm.NewObject();

        realm.DefineValue(classList, "contains",
            realm.NewMethod("contains", (in call) => Contains(element, in call), 1));

        realm.DefineValue(classList, "add",
            realm.NewMethod("add", (in call) => Add(element, onClassChanged, in call)));

        realm.DefineValue(classList, "remove",
            realm.NewMethod("remove", (in call) => Remove(element, onClassChanged, in call)));

        realm.DefineValue(classList, "toggle",
            realm.NewMethod("toggle", (in call) => Toggle(element, onClassChanged, in call), 1));

        realm.DefineValue(classList, "replace",
            realm.NewMethod("replace", (in call) => Replace(element, onClassChanged, in call), 2));

        return classList;
    }

    private static JsValue Contains(DomElement element, in JsCall call)
    {
        // No argument at all answers false without coercing: there is nothing to convert, and
        // ToJsString of a missing value is not a question the realm should be asked.
        if (call.Length == 0)
            return JsValue.False;
        return JsValue.Boolean(new DomTokenList(element, "class").Contains(call.Realm.ToJsString(call[0])));
    }

    private static JsValue Add(DomElement element, Action<DomElement>? onClassChanged, in JsCall call)
    {
        var tokens = new List<string>();
        for (var i = 0; i < call.Length; i++)
        {
            // ToJsString, not the handle's rendering: an object argument must run its own toString,
            // which is the coercion a page observes here.
            var cls = call.Realm.ToJsString(call[i]);
            if (!string.IsNullOrEmpty(cls))
                tokens.Add(cls);
        }

        new DomTokenList(element, "class").Add([.. tokens]);
        onClassChanged?.Invoke(element);
        return JsValue.Undefined;
    }

    private static JsValue Remove(DomElement element, Action<DomElement>? onClassChanged, in JsCall call)
    {
        var tokens = new List<string>();
        for (var i = 0; i < call.Length; i++)
        {
            var cls = call.Realm.ToJsString(call[i]);
            if (!string.IsNullOrEmpty(cls))
                tokens.Add(cls);
        }

        new DomTokenList(element, "class").Remove([.. tokens]);
        onClassChanged?.Invoke(element);
        return JsValue.Undefined;
    }

    private static JsValue Toggle(DomElement element, Action<DomElement>? onClassChanged, in JsCall call)
    {
        if (call.Length == 0)
            return JsValue.False;
        var cls = call.Realm.ToJsString(call[0]);

        // toggle(name) and toggle(name, undefined) are the same call: an explicitly passed undefined
        // leaves the force flag unset, so the token flips. Arity alone is not the test.
        bool? force = call.Length >= 2 && !call[1].IsUndefined ? call[1].AsBoolean : null;
        var present = new DomTokenList(element, "class").Toggle(cls, force);
        onClassChanged?.Invoke(element);
        return JsValue.Boolean(present);
    }

    private static JsValue Replace(DomElement element, Action<DomElement>? onClassChanged, in JsCall call)
    {
        if (call.Length < 2)
            return JsValue.False;
        var replaced = new DomTokenList(element, "class")
            .Replace(call.Realm.ToJsString(call[0]), call.Realm.ToJsString(call[1]));
        if (replaced)
            onClassChanged?.Invoke(element);
        return JsValue.Boolean(replaced);
    }
}
