using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The <c>document</c> node-factory methods — <c>createElement</c>, <c>createTextNode</c>,
/// <c>createDocumentFragment</c>, <c>createElementNS</c>, <c>createAttribute</c>,
/// <c>createAttributeNS</c> — co-located as an HtmlBridge feature module (Phase 3). Each validates
/// its name argument (through the <see cref="IDocumentFactoryHost"/> contract), constructs the
/// canonical node through the same funnel, and returns its JS wrapper. Previously the bridge's
/// <c>JsRegistrationCreateElement014Core</c> etc. in the shared JsFunctionCallbacks/Registration.cs
/// grab-bag. The document-level factories (<c>createDocument</c>, <c>createHTMLDocument</c>,
/// <c>createDocumentType</c>) and <c>createEvent</c> are browsing-context / event-object concerns and
/// are not part of this slice.
/// </summary>
/// <remarks>
/// Everything here is JSEAL's: <c>DomBridge/Registration/Document.cs</c> mints all seven members
/// through the realm, so the script context this module used to take alongside its host is gone with
/// the frame. It was there for two things and both moved rather than disappeared — the name
/// validations are on the host contract, and the DOM exceptions go through <c>JsCall.Realm</c>'s own
/// <c>DomError</c>, which builds the same object against the same <c>DOMException</c> global. Every
/// argument coercion is the realm's <c>ToJsString</c>, which is the observable ECMAScript
/// <c>ToString</c> the engine's <c>ToString()</c> ran here before.
/// </remarks>
internal static class DocumentFactoryBinding
{
    public static JsValue CreateElement(IDocumentFactoryHost host, in JsCall call)
    {
        if (call.Length == 0)
            throw call.Realm.Error(JsErrorKind.Error, "Failed to execute 'createElement': 1 argument required, but only 0 present.");
        var tag = call.Realm.ToJsString(call[0]);
        host.ValidateElementName(tag);
        tag = DomBridge.AsciiToLower(tag);

        // The options argument's only member is `is`, which asks for a customized built-in: the
        // element is still a <button>, and the definition it belongs to is the one that name selects.
        string? isValue = null;
        if (call[1].IsObject)
        {
            var requested = call.Realm.GetProperty(call[1], "is");
            if (!requested.IsNullish)
                isValue = call.Realm.ToJsString(requested);
        }

        // A defined custom element is created by running its own constructor, which is what makes
        // document.createElement('x-thing') an instance of the class rather than a plain element
        // that happens to carry the tag (HTML 4.13.6).
        var upgraded = host.CreateDefinedCustomElement(tag, isValue);
        if (!upgraded.IsMissing)
            return upgraded;

        var el = host.CreateBridgeElement(tag);
        if (isValue is not null)
        {
            // Nothing is defined for this name yet. The is value is still the element's — a later
            // define() upgrades it, and a browser serializes it meanwhile: measured,
            // createElement('button', {is: 'nope'}) reports <button is="nope"> while
            // getAttribute('is') stays null.
            host.RecordCustomElementIsValue(el, isValue);
        }

        return host.WrapNode(el);
    }

    /// <summary>
    /// <c>document.adoptNode(node)</c> — moves argument zero and its subtree into this document,
    /// removing it from wherever it was (DOM §4.5). Unlike <c>importNode</c> it is the same node
    /// afterwards, not a copy, which is what makes it observable to a custom element: an upgraded one
    /// in the moved subtree receives <c>adoptedCallback(oldDocument, newDocument)</c>.
    /// </summary>
    public static JsValue AdoptNode(IDocumentFactoryHost host, in JsCall call)
    {
        if (!call[0].IsObject || host.FindDomNode(call[0]) is not { } node)
            throw call.Realm.DomError("TypeError", "adoptNode requires a node to adopt.");

        if (node is Broiler.Dom.DomDocument)
        {
            throw call.Realm.DomError(
                "NotSupportedError",
                "Failed to execute 'adoptNode' on 'Document': The node provided is of type '#document', which may not be adopted.");
        }

        return host.WrapNode(host.AdoptNode(node));
    }

    public static JsValue CreateTextNode(IDocumentFactoryHost host, in JsCall call)
    {
        var text = call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty;
        return host.WrapNode(host.CreateBridgeTextNode(text));
    }

    public static JsValue CreateDocumentFragment(IDocumentFactoryHost host, in JsCall call)
        => host.WrapNode(host.CreateBridgeDocumentFragment());

    /// <summary>
    /// <c>document.importNode(node, deep)</c> — a copy of argument zero owned by this document.
    /// <c>deep</c> defaults to <see langword="false"/>, so a bare <c>importNode(node)</c> copies the
    /// node alone; the idiom that matters, <c>importNode(template.content, true)</c>, copies the whole
    /// stamped subtree. Every node this bridge mints already belongs to the one document, so importing
    /// is exactly a clone — there is no adoption step to perform.
    /// </summary>
    public static JsValue ImportNode(IDocumentFactoryHost host, in JsCall call)
    {
        if (!call[0].IsObject)
            throw call.Realm.DomError("TypeError", "importNode requires a node to import.");

        Broiler.Dom.DomNode? node = host.FindDomNode(call[0]);
        if (node is null)
            throw call.Realm.DomError("TypeError", "importNode was passed something that is not a node.");

        bool deep = call[1].AsBoolean;
        return host.WrapNode(host.CloneDomNode(node, deep));
    }

    public static JsValue CreateElementNS(IDocumentFactoryHost host, in JsCall call)
    {
        var ns = call.Length > 0 && !call[0].IsNullish ? call.Realm.ToJsString(call[0]) : null;
        var localName = call.Length > 1
            ? call.Realm.ToJsString(call[1])
            : (call.Length > 0 ? call.Realm.ToJsString(call[0]) : "div");
        host.ValidateQualifiedName(localName, ns);
        var el = string.IsNullOrEmpty(ns)
            ? host.CreateBridgeElement(localName)
            : host.CreateBridgeElementNS(ns, localName);
        return host.WrapNode(el);
    }

    public static JsValue CreateAttribute(IDocumentFactoryHost host, in JsCall call)
    {
        if (call.Length == 0)
            throw call.Realm.Error(JsErrorKind.Error, "Failed to execute 'createAttribute': 1 argument required, but only 0 present.");
        var name = call.Realm.ToJsString(call[0]);
        host.ValidateElementName(name);
        return host.BuildStandaloneAttrNode(DomBridge.AsciiToLower(name), null);
    }

    public static JsValue CreateAttributeNS(IDocumentFactoryHost host, in JsCall call)
    {
        var ns = call.Length > 0 && !call[0].IsNullish ? call.Realm.ToJsString(call[0]) : null;
        if (call.Length < 2)
            throw call.Realm.Error(JsErrorKind.Error, "Failed to execute 'createAttributeNS': 2 arguments required, but fewer present.");
        var qualifiedName = call.Realm.ToJsString(call[1]);
        host.ValidateQualifiedName(qualifiedName, ns);
        return host.BuildStandaloneAttrNode(qualifiedName, ns);
    }
}
