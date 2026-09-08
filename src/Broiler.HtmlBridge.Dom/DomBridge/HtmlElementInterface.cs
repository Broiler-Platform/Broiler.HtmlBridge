using System.Runtime.CompilerServices;

using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge;

/// <summary>
/// <c>HTMLElement</c> as a real interface: the members HTML gives every HTML element — and the mixins
/// it includes — on <c>HTMLElement.prototype</c> rather than copied onto every element wrapper.
/// </summary>
/// <remarks>
/// <para>
/// The sixth instalment of track 6's wrapper item, and the direct sequel to
/// <c>DomBridge.ElementInterface.cs</c>, whose <see cref="Dom.Features.JsElementSource"/> mechanism it
/// reuses unchanged: each member is written once and installed either on the prototype, where it
/// resolves its element from the receiver, or on one wrapper, where it closes over the element it was
/// built for. <c>HTMLElement.prototype</c> owned nothing but its <c>constructor</c>; it owns 37
/// members now, and an element is down from 77 own properties to 40.
/// </para>
/// <para>
/// <b>What moves is Web IDL's <c>HTMLElement</c>, plus the mixins it includes</b> —
/// <c>ElementCSSInlineStyle</c> (<c>style</c>), <c>HTMLOrSVGElement</c> (<c>dataset</c>,
/// <c>tabIndex</c>, <c>focus</c>, <c>blur</c>), <c>GlobalEventHandlers</c> (the seventeen <c>on*</c>
/// reflectors) and the CSSOM View <c>offset*</c> metrics. Not the per-control reflectors beside them:
/// <c>value</c>, <c>checked</c>, <c>type</c>, <c>name</c>, <c>disabled</c>, <c>required</c> and
/// <c>files</c> are installed on every element here where a browser gives them only to the interfaces
/// that declare them, so relocating them is a decision about dropping them from a <c>&lt;div&gt;</c>
/// rather than a relocation. <c>textContent</c> stays each wrapper's own for the reason it always has.
/// </para>
/// <para>
/// <b>An SVG element keeps its own copies.</b> It does not inherit <c>HTMLElement.prototype</c> —
/// <c>SVGElement</c> derives straight from <c>Element</c> — so it installs the same members on itself,
/// exactly as it did before. That is deliberately today's behaviour and not a browser's: an
/// <c>SVGElement</c> shares only three of these mixins and has no <c>title</c>, <c>innerText</c> or
/// <c>offsetWidth</c>. Narrowing it is the per-tag SVG interface decision this track already holds
/// open, and doing it here would mean minting a prototype shape from a specification reading rather
/// than from a measurement.
/// </para>
/// <para>
/// <b>Two members are per-instance objects</b> and needed the treatment <c>classList</c> got:
/// <c>style</c> was a declaration built with the wrapper and captured by the accessor, and
/// <c>dataset</c> a self-replacing accessor that wrote its map back onto the wrapper it closed over.
/// Both are weak per-element caches now, so <c>el.style === el.style</c> and
/// <c>el.dataset === el.dataset</c> hold while the element itself carries neither.
/// </para>
/// <para>
/// <b>The installer speaks JSEAL.</b> Three neighbours still take the engine's argument frame and are
/// named where they appear — the form-control reflectors
/// (<see cref="Dom.Features.FormControlBinding"/>) and <c>click</c>/<c>focus</c>/<c>blur</c>
/// (<see cref="Dom.Features.EventTargetBinding"/>, whose members are installed from unmigrated files
/// too) — plus the wrapper entry point below, which <c>DomBridge/JsObjects.cs</c> hands an engine
/// object. Everything else is minted through <see cref="Realm"/> onto a handle over the same object,
/// which is a cast rather than a conversion, so the members land in the order they are written in.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    /// <summary>
    /// Whether <c>HTMLElement.prototype</c> carries the interface yet, which is what lets an HTML
    /// element's wrapper stop installing its own copy of it.
    /// </summary>
    private bool _htmlElementInterfacePrototypeReady;

    /// <summary>One inline <c>CSSStyleDeclaration</c> per element, so <c>el.style === el.style</c>.</summary>
    /// <remarks>
    /// The declaration is a live object — it writes each mutation through to the <c>style</c> content
    /// attribute and invalidates the style scope — so a second instance would be redundant rather than
    /// fresher, and rebuilding one per read would drop whatever the page had set on it. The box is
    /// because a <see cref="ConditionalWeakTable{TKey,TValue}"/> value must be a reference type and a
    /// <see cref="JsValue"/> handle is a struct.
    /// </remarks>
    private readonly ConditionalWeakTable<DomElement, StrongBox<JsValue>> _inlineStyles = new();

    /// <summary>One <c>DOMStringMap</c> per element, on the same terms.</summary>
    /// <remarks>
    /// Built on first read rather than with the element: a document has thousands of elements and few
    /// are ever asked for their dataset, so building every map up front would allocate a proxy and
    /// four callbacks per element for nothing. That was the reason the instance version was a
    /// self-replacing accessor; a weak table gives the same laziness without leaving an own property
    /// behind.
    /// </remarks>
    private readonly ConditionalWeakTable<DomElement, StrongBox<JsValue>> _datasets = new();

    /// <summary>
    /// Installs <c>HTMLElement</c>'s members on <c>HTMLElement.prototype</c>. A no-op when the realm
    /// does not carry the interface.
    /// </summary>
    internal void RegisterHtmlElementInterface()
    {
        if (PrototypeOfInterface("HTMLElement") is not { } proto)
            return;

        InstallHtmlElementInterface(Dom.Runtime.JsInterop.FromEngineObject(proto), RequireElementReceiver);
        _htmlElementInterfacePrototypeReady = true;
    }

    /// <summary>
    /// <c>HTMLElement</c>'s members as own properties of one wrapper — for an SVG element, which does
    /// not inherit the interface, and for a wrapper minted before the realm carried it.
    /// </summary>
    /// <remarks>
    /// Engine-typed because its caller is: <c>DomBridge/JsObjects.cs</c> mints the wrapper and holds
    /// it as the engine's own object. The seam is a cast, so the handle below is that object.
    /// </remarks>
    private void PopulateHtmlElementInterfaceOnInstance(JSObject obj, DomElement element)
    {
        InstallHtmlElementInterface(
            Dom.Runtime.JsInterop.FromEngineObject(obj), (in JsCall _, string _) => element);
    }

    /// <summary>
    /// The whole <c>HTMLElement</c> interface onto <paramref name="target"/> —
    /// <c>HTMLElement.prototype</c>, or one wrapper that cannot inherit from it.
    /// </summary>
    private void InstallHtmlElementInterface(JsValue target, Dom.Features.JsElementSource element)
    {
        Dom.Features.GlobalAttributeBinding.InstallHtmlElementMembers(this, Realm, target, element);
        Dom.Features.ElementContentBinding.InstallHtmlElementMembers(this, Realm, target, element);

        // The form-control reflectors still install with FastAddValue against the engine's own object
        // and argument frame, so the module is handed both — the same object the realm is installing
        // on, and the same resolution rule under the frame its members read.
        var engineTarget = Dom.Runtime.JsInterop.ToEngineObject(target);
        var engineElement = EngineSourceOf(element);
        _formControl.InstallHtmlElementMembers(engineTarget, engineElement);

        // style — ElementCSSInlineStyle. Assigning a string sets cssText rather than replacing the
        // object, which is why the setter is here and not a plain data property.
        Realm.DefineAccessor(target, "style",
            (in call) => InlineStyleFor(element(in call, "style")),
            (in call) =>
            {
                // The receiver is resolved before the value is looked at, as it was when the engine
                // frame carried the string test inside the callee: assigning a non-string to the
                // setter with a receiver that is not an element still raises the TypeError.
                var styled = element(in call, "style");

                // Only a string right-hand side acts — a quirk preserved verbatim from the bridge's
                // original element.style setter, and the reason StyleDeclarationBinding keeps its
                // string-typed overload beside the cssText one that stringifies anything.
                if (call.Length > 0 && call[0].IsString)
                {
                    Dom.Features.StyleDeclarationBinding.SetInlineStyleCssText(
                        this, styled, InlineStyleMutation(styled), call[0].AsString!);
                }

                return JsValue.Undefined;
            });

        // dataset — HTMLOrSVGElement's live DOMStringMap over the data-* attributes.
        Realm.DefineAccessor(target, "dataset",
            (in call) => DatasetFor(element(in call, "dataset")), null);

        // click/focus/blur are EventTargetBinding's, and that module's bodies read the engine frame
        // because unmigrated files install the same members elsewhere; they land on this same object.
        AddPrototypeMethod(engineTarget, "click", 0,
            (in Arguments a) => Dom.Features.EventTargetBinding.Click(this, engineElement(in a, "click"), in a));
        AddPrototypeMethod(engineTarget, "focus", 0,
            (in Arguments a) => Dom.Features.EventTargetBinding.Focus(this, engineElement(in a, "focus"), in a));
        AddPrototypeMethod(engineTarget, "blur", 0,
            (in Arguments a) => Dom.Features.EventTargetBinding.Blur(this, engineElement(in a, "blur"), in a));

        // attachInternals() — HTML §4.13.5, a member of HTMLElement rather than of the custom
        // elements only, which is what makes the standard feature-detect answer the right way. It
        // refuses at call time for an element that is not a form-associated custom element.
        AddInterfaceMethod(target, "attachInternals", 0,
            (in call) => ElementInternals.AttachInternals(element(in call, "attachInternals")));

        InstallInlineEventHandlerMembers(target, element);

        Dom.Features.ElementGeometryBinding.InstallHtmlElementMembers(this, Realm, target, element);
    }

    /// <summary>
    /// The <c>GlobalEventHandlers</c> reflectors — <c>onclick</c>, <c>onload</c> and the rest of
    /// <see cref="InlineEventNames"/>.
    /// </summary>
    private void InstallInlineEventHandlerMembers(JsValue target, Dom.Features.JsElementSource element)
    {
        foreach (var name in InlineEventNames)
        {
            // Captured per iteration: the loop variable is one shared binding by the time a handler
            // runs, so reading it inside the closure would give every reflector the last name.
            var eventName = name;
            var member = "on" + eventName;

            // EventHandlerReflectorBinding is migrated, so the realm mints and names the pair itself.
            // Minting them through the realm rather than wrapping a bridge function object around it is
            // what gives the setter a properly tagged argument, and so lets "is it a function" — the
            // whole of what the setter decides — stay the binding's question.
            Realm.DefineAccessor(target, member,
                (in call) => Dom.Features.EventHandlerReflectorBinding.GetOn(
                    this, element(in call, member), eventName, in call),
                (in call) => Dom.Features.EventHandlerReflectorBinding.SetOn(
                    this, element(in call, member), eventName, in call));
        }
    }

    /// <summary>The element's one inline style declaration, built on first use.</summary>
    private JsValue InlineStyleFor(DomElement element) =>
        _inlineStyles.GetValue(element, key => new StrongBox<JsValue>(
            Dom.Features.StyleDeclarationBinding.BuildInlineDeclaration(
                Realm, this, key, InlineStyleMutation(key),
                onPositionAreaInvalidate: ClearPositionAreaResolution))).Value;

    /// <summary>
    /// What every inline-style mutation owes: write the dict through to the canonical <c>style</c>
    /// attribute, so <c>el.style</c> and <c>getAttribute("style")</c> observe one state, then
    /// invalidate the computed style.
    /// </summary>
    /// <remarks>
    /// Both the declaration object's own mutations (per property, <c>cssText</c>, <c>setProperty</c>,
    /// <c>removeProperty</c>, <c>cssFloat</c>) and the <c>el.style = "…"</c> assignment run it.
    /// </remarks>
    private Action InlineStyleMutation(DomElement element) => () =>
    {
        SyncStyleAttributeFromInlineStyle(element);
        InvalidateStyleScope(element);
    };

    /// <summary>
    /// The element's one <c>DOMStringMap</c>, built on first use — or <see langword="undefined"/> when
    /// the realm has no <c>Proxy</c> to build it from, which is honest: an absent dataset is at least
    /// not one that silently drops writes.
    /// </summary>
    private JsValue DatasetFor(DomElement element)
    {
        if (_datasets.TryGetValue(element, out var cached))
            return cached.Value;

        // The context guard is unchanged in effect — the realm and the context are adopted together
        // and cleared together — and a realm with no Proxy still answers undefined rather than a map
        // that would silently drop writes.
        if (_jsContext is null ||
            Dom.Features.DatasetBinding.Build(Realm, element, InvalidateStyleScope) is not { IsObject: true } dataset)
        {
            return JsValue.Undefined;
        }

        _datasets.Add(element, new StrongBox<JsValue>(dataset));
        return dataset;
    }
}
