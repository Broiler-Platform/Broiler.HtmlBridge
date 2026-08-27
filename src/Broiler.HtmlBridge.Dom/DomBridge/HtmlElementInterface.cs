using System.Runtime.CompilerServices;

using Broiler.Dom;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;

namespace Broiler.HtmlBridge;

/// <summary>
/// <c>HTMLElement</c> as a real interface: the members HTML gives every HTML element — and the mixins
/// it includes — on <c>HTMLElement.prototype</c> rather than copied onto every element wrapper.
/// </summary>
/// <remarks>
/// <para>
/// The sixth instalment of track 6's wrapper item, and the direct sequel to
/// <c>DomBridge.ElementInterface.cs</c>, whose <see cref="Dom.Features.ElementSource"/> mechanism it
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
    /// fresher, and rebuilding one per read would drop whatever the page had set on it.
    /// </remarks>
    private readonly ConditionalWeakTable<DomElement, JSObject> _inlineStyles = new();

    /// <summary>One <c>DOMStringMap</c> per element, on the same terms.</summary>
    /// <remarks>
    /// Built on first read rather than with the element: a document has thousands of elements and few
    /// are ever asked for their dataset, so building every map up front would allocate a proxy and
    /// four callbacks per element for nothing. That was the reason the instance version was a
    /// self-replacing accessor; a weak table gives the same laziness without leaving an own property
    /// behind.
    /// </remarks>
    private readonly ConditionalWeakTable<DomElement, JSObject> _datasets = new();

    /// <summary>
    /// Installs <c>HTMLElement</c>'s members on <c>HTMLElement.prototype</c>. A no-op when the realm
    /// does not carry the interface.
    /// </summary>
    internal void RegisterHtmlElementInterface()
    {
        if (PrototypeOfInterface("HTMLElement") is not { } proto)
            return;

        InstallHtmlElementInterface(proto, RequireElementReceiver);
        _htmlElementInterfacePrototypeReady = true;
    }

    /// <summary>
    /// <c>HTMLElement</c>'s members as own properties of one wrapper — for an SVG element, which does
    /// not inherit the interface, and for a wrapper minted before the realm carried it.
    /// </summary>
    private void PopulateHtmlElementInterfaceOnInstance(JSObject obj, DomElement element)
    {
        InstallHtmlElementInterface(obj, (in Arguments _, string _) => element);
    }

    /// <summary>
    /// The whole <c>HTMLElement</c> interface onto <paramref name="target"/> —
    /// <c>HTMLElement.prototype</c>, or one wrapper that cannot inherit from it.
    /// </summary>
    private void InstallHtmlElementInterface(JSObject target, Dom.Features.ElementSource element)
    {
        Dom.Features.GlobalAttributeBinding.InstallHtmlElementMembers(this, target, element);
        Dom.Features.ElementContentBinding.InstallHtmlElementMembers(this, target, element);
        _formControl.InstallHtmlElementMembers(target, element);

        // style — ElementCSSInlineStyle. Assigning a string sets cssText rather than replacing the
        // object, which is why the setter is here and not a plain data property.
        AddPrototypeAccessor(target, "style",
            (in Arguments a) => InlineStyleFor(element(in a, "style")),
            (in Arguments a) => Dom.Features.StyleDeclarationBinding.SetInlineStyleCssText(
                this, element(in a, "style"), InlineStyleMutation(element(in a, "style")), in a));

        // dataset — HTMLOrSVGElement's live DOMStringMap over the data-* attributes.
        AddPrototypeAccessor(target, "dataset",
            (in Arguments a) => DatasetFor(element(in a, "dataset")));

        AddPrototypeMethod(target, "click", 0,
            (in Arguments a) => Dom.Features.EventTargetBinding.Click(this, element(in a, "click"), in a));
        AddPrototypeMethod(target, "focus", 0,
            (in Arguments a) => Dom.Features.EventTargetBinding.Focus(this, element(in a, "focus"), in a));
        AddPrototypeMethod(target, "blur", 0,
            (in Arguments a) => Dom.Features.EventTargetBinding.Blur(this, element(in a, "blur"), in a));

        // attachInternals() — HTML §4.13.5, a member of HTMLElement rather than of the custom
        // elements only, which is what makes the standard feature-detect answer the right way. It
        // refuses at call time for an element that is not a form-associated custom element.
        AddPrototypeMethod(target, "attachInternals", 0,
            (in Arguments a) => ElementInternals.AttachInternals(element(in a, "attachInternals"), in a));

        InstallInlineEventHandlerMembers(target, element);

        Dom.Features.ElementGeometryBinding.InstallHtmlElementMembers(this, target, element);
    }

    /// <summary>
    /// The <c>GlobalEventHandlers</c> reflectors — <c>onclick</c>, <c>onload</c> and the rest of
    /// <see cref="InlineEventNames"/>.
    /// </summary>
    private void InstallInlineEventHandlerMembers(JSObject target, Dom.Features.ElementSource element)
    {
        foreach (var name in InlineEventNames)
        {
            // Captured per iteration: the loop variable is one shared binding by the time a handler
            // runs, so reading it inside the closure would give every reflector the last name.
            var eventName = name;
            var member = "on" + eventName;

            target.FastAddProperty(member,
                new DomFunction((in Arguments a) =>
                    Dom.Features.EventHandlerReflectorBinding.GetOn(this, element(in a, member), eventName, in a), "get " + member),
                new DomFunction((in Arguments a) =>
                    Dom.Features.EventHandlerReflectorBinding.SetOn(this, element(in a, member), eventName, in a), "set " + member),
                JSPropertyAttributes.EnumerableConfigurableProperty);
        }
    }

    /// <summary>The element's one inline style declaration, built on first use.</summary>
    private JSObject InlineStyleFor(DomElement element) =>
        _inlineStyles.GetValue(element, key => Dom.Features.StyleDeclarationBinding.BuildInlineDeclaration(
            this, key, InlineStyleMutation(key), onPositionAreaInvalidate: ClearPositionAreaResolution));

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
    private JSValue DatasetFor(DomElement element)
    {
        if (_datasets.TryGetValue(element, out var cached))
            return cached;

        if (_jsContext is not { } context ||
            Dom.Features.DatasetBinding.Build(context, element, InvalidateStyleScope) is not { } dataset)
        {
            return JSUndefined.Value;
        }

        _datasets.Add(element, dataset);
        return dataset;
    }
}
