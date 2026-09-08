using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge;

// Explicit IDocumentQueryHost implementation for the DocumentQueryBinding feature module (Phase 3):
// the bridge exposes the document root, the document-order element list, the JS-wrapper factory,
// selector validation and the two collection factories via explicit interface members, so the module
// never reaches an arbitrary bridge private field and the public surface is unchanged.
//
// The contract above is spelled in JSEAL; this file is where that meets the half of the bridge that is
// still engine-typed. Three things on the other side of the seam are unmigrated — ToJSObject (the
// wrapper factory), ValidateSelector (which raises its DOMException against the script context) and
// DomCollectionBinding (which builds NodeList/HTMLCollection over a List<JSValue>) — so the handles
// are unwrapped here and nowhere above. JsInterop is a cast, not a conversion: a handle carries the
// engine's own object, so wrapper identity and `el === el` are unaffected by the round trip.
public sealed partial class DomBridge : Dom.Features.IDocumentQueryHost
{
    JsValue Dom.Features.IDocumentQueryHost.ToJsObject(DomNode node) =>
        Dom.Runtime.JsInterop.FromEngineObject(ToJSObject(node));

    DomElement Dom.Features.IDocumentQueryHost.DocumentElement => DocumentElement;

    IReadOnlyList<DomElement> Dom.Features.IDocumentQueryHost.Elements => Elements;

    bool Dom.Features.IDocumentQueryHost.MatchesSelector(DomElement element, string selector, DomElement? scope)
        => MatchesSelector(element, selector, scope);

    // The context is the diagnostic sink the DOMException is constructed against, and a null one (no
    // bridge attached yet) means the validation is skipped — exactly as when the module passed
    // host.JsContext straight back to this helper.
    void Dom.Features.IDocumentQueryHost.ValidateSelector(string selector) =>
        ValidateSelector(selector, _jsContext);

    JsValue Dom.Features.IDocumentQueryHost.NodeList(Func<List<JsValue>> contents) =>
        AdoptQueryCollection(Dom.Features.DomCollectionBinding.NodeList(_jsContext, () => ToQueryCollectionItems(contents())));

    JsValue Dom.Features.IDocumentQueryHost.HtmlCollection(
        Func<List<JsValue>> contents, Func<string, JsValue?>? namedLookup) =>
        AdoptQueryCollection(Dom.Features.DomCollectionBinding.HtmlCollection(
            _jsContext,
            () => ToQueryCollectionItems(contents()),
            namedLookup is null
                ? null
                : name => namedLookup(name) is { } named ? Dom.Runtime.JsInterop.ToEngineValue(named) : null));

    /// <summary>The module's wrapper handles as the engine values the collection stores.</summary>
    private static List<JSValue> ToQueryCollectionItems(List<JsValue> values)
    {
        var engineValues = new List<JSValue>(values.Count);
        foreach (var value in values)
        {
            // Every member of a query result is an element wrapper, so the cast cannot fail; a
            // primitive would mean the module produced something a collection cannot hold.
            engineValues.Add(Dom.Runtime.JsInterop.ToEngineObject(value));
        }

        return engineValues;
    }

    /// <summary>A handle over a collection the unmigrated collection builder produced.</summary>
    /// <remarks>
    /// Its static type is <c>JSValue</c> and its dynamic type is always the <c>DomCollection</c> object
    /// — the builder has one return statement — so the pattern is a type-narrowing, not a branch that
    /// is expected to fall through. The fallback is <c>undefined</c> rather than a throw because a
    /// query answering an empty-ish value is a better outcome for a page than an exception thrown from
    /// inside a getter.
    /// </remarks>
    private static JsValue AdoptQueryCollection(JSValue collection) =>
        collection is JSObject @object ? Dom.Runtime.JsInterop.FromEngineObject(@object) : JsValue.Undefined;
}
