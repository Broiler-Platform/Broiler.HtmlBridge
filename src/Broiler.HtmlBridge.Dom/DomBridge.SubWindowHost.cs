using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Dom.Features;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

/// <summary>
/// <see cref="DomBridge"/>'s implementation of <see cref="ISubWindowHost"/>, the contract the extracted
/// <see cref="Broiler.HtmlBridge.Dom.Features.SubWindowBinding"/> feature module consumes (HtmlBridge
/// complexity-reduction roadmap Phase 3, P3.17). Explicit interface members, so these seams do not widen
/// the public <c>DomBridge</c> surface. The module owns the sub-window object and its scroll/
/// getComputedStyle surface; the bridge keeps the sub-document builder, resource loading and scroll
/// geometry it reaches through here.
/// </summary>
/// <remarks>
/// The contract is spelled in JSEAL and one bridge member behind it is not: <c>_windowJSObject</c> is
/// still the engine's own object, so <c>MainWindow</c> below casts it up through
/// <see cref="Dom.Runtime.JsInterop"/>, and that is the last crossing in this file. The sub-document,
/// the computed-style object and the element lookup all forward a handle. A cast is not a conversion
/// — a handle carries the engine's own object — so what the module receives is what the bridge's own
/// caches hold.
/// </remarks>
public sealed partial class DomBridge : ISubWindowHost
{
    IJsRealm ISubWindowHost.Realm => Realm;

    // Missing rather than undefined for "there is no window yet": the module tests it with IsObject
    // and never hands it to script, which is what the null check it replaces did.
    JsValue ISubWindowHost.MainWindow =>
        _windowJSObject is { } window ? Dom.Runtime.JsInterop.FromEngineObject(window) : JsValue.Missing;

    JsValue ISubWindowHost.GetOrCreateSubDocument(DomElement container) =>
        GetOrCreateSubDocument(container);

    DomDocument? ISubWindowHost.GetContentDocument(DomElement container) => GetContentDocument(container);

    DomElement? ISubWindowHost.GetFrameForContentDocument(DomNode? owningDocument) =>
        GetFrameForContentDocument(owningDocument);

    string ISubWindowHost.ResolveSubResourceUrl(string resourceUrl, string? baseUrl) =>
        ResolveSubResourceUrl(resourceUrl, baseUrl);

    string ISubWindowHost.GetInheritedSubDocumentBaseUrl(DomElement container) =>
        GetInheritedSubDocumentBaseUrl(container);

    double ISubWindowHost.GetElementScrollOffset(DomElement element, bool vertical) =>
        GetElementScrollOffset(element, vertical);

    void ISubWindowHost.SetElementScroll(DomElement element, double? left, double? top, bool relative, string? behavior) =>
        SetElementScrollOffsetsWithBehavior(element, left, top, relative: relative, clamp: false, behavior: behavior);

    /// <summary>
    /// <c>scroll(x, y)</c> / <c>scroll({ left, top, behavior })</c>, read off a migrated call frame.
    /// </summary>
    /// <remarks>
    /// The one reading both scroll contracts share — an options object wins over positional
    /// coordinates, an absent or nullish member is "leave this axis alone", and a blank behaviour is
    /// none — over JSEAL values rather than engine ones, because these callbacks no longer have an
    /// engine argument frame to hand over. The coercions are the realm's for the same reason they
    /// were the engine's before: <c>scrollTo("100", "200")</c> is a page passing strings.
    /// <c>DomBridge.WindowScrollHost.cs</c> forwards its own contract's member here rather than
    /// keeping a second copy, which is why the member's name has to be changed in both contracts at
    /// once or not at all.
    /// </remarks>
    (double? Left, double? Top, string? Behavior) ISubWindowHost.GetScrollArguments(ReadOnlySpan<JsValue> supplied)
    {
        if (supplied.Length == 0)
            return (null, null, null);

        var realm = Realm;
        if (supplied[0].IsObject)
        {
            var options = supplied[0];
            return (
                ScrollCoordinateOption(realm, options, "left"),
                ScrollCoordinateOption(realm, options, "top"),
                ScrollBehaviorOption(realm, options));
        }

        return (
            realm.ToNumber(supplied[0]),
            supplied.Length > 1 ? realm.ToNumber(supplied[1]) : null,
            null);
    }

    private static double? ScrollCoordinateOption(IJsRealm realm, JsValue options, string propertyName)
    {
        var value = realm.GetProperty(options, propertyName);
        return value.IsNullish ? null : realm.ToNumber(value);
    }

    private static string? ScrollBehaviorOption(IJsRealm realm, JsValue options)
    {
        var value = realm.GetProperty(options, "behavior");
        if (value.IsNullish)
            return null;

        var behavior = realm.ToJsString(value);
        return string.IsNullOrWhiteSpace(behavior) ? null : behavior;
    }

    // A plain forward: the reverse lookup takes the same handle. The module guards with IsObject
    // before asking, and a handle that is not an object would answer null rather than throw.
    DomElement? ISubWindowHost.FindElement(JsValue wrapper) =>
        FindDomElementByJSObject(wrapper);

    // The computed-style builder is migrated, so this one hands the handle straight through.
    JsValue ISubWindowHost.BuildComputedStyleObject(DomElement? element, string? pseudoElement) =>
        BuildComputedStyleObject(element, pseudoElement);

    /// <summary>
    /// A global of this realm, for the sub-window's mirror list.
    /// </summary>
    /// <remarks>
    /// The read is the same one <c>_jsContext[name]</c> performed — the global object <em>is</em> the
    /// context under this engine (<see cref="JsCapabilities.GlobalIsVariableScope"/>) — so a name the
    /// realm does not define answers <c>undefined</c> and is mirrored as such. The <see langword="false"/>
    /// result means only that there is no realm at all, which is the case the null-conditional it
    /// replaces was guarding.
    /// </remarks>
    bool ISubWindowHost.TryGetGlobal(string name, out JsValue value)
    {
        if (_realm is not { } realm)
        {
            value = JsValue.Missing;
            return false;
        }

        value = realm.GetProperty(realm.Global, name);
        return true;
    }

    void ISubWindowHost.PublishPendingSubDocumentGlobals(DomElement containerElement, JsValue subWindow) =>
        PublishPendingSubDocumentGlobals(containerElement, subWindow);
}
