using Broiler.Dom;
using Broiler.JavaScript.Engine;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;

namespace Broiler.HtmlBridge;

/// <summary>
/// Registers the Custom Elements surface: the <c>CustomElementRegistry</c> interface, the
/// <c>customElements</c> instance, the constructible <c>HTMLElement</c> base, and the subscription
/// that turns canonical DOM mutations into reaction callbacks.
/// </summary>
public sealed partial class DomBridge
{
    private Dom.Features.CustomElementsBinding? _customElements;

    internal Dom.Features.CustomElementsBinding CustomElements =>
        _customElements ??= new Dom.Features.CustomElementsBinding(this);

    /// <summary>
    /// Replaces the non-constructible <c>HTMLElement</c> with the base a custom element extends, and
    /// installs the registry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The base is JavaScript for one reason: it needs <c>new.target</c>. <c>new X()</c> runs
    /// <c>X</c>'s constructor, which calls <c>super()</c>, and only <c>new.target</c> says which
    /// subclass is being constructed — so which prototype the element must get and, through the
    /// registry, which tag name it has. A host function cannot see it, the engine's
    /// <c>Arguments</c> not carrying one, so the base reads it here and calls the host for the
    /// element itself.
    /// </para>
    /// <para>
    /// Returning an object from a base constructor is what makes this work: <c>super()</c>'s result
    /// becomes <c>this</c>, so the subclass constructor goes on to run against a real DOM element.
    /// The element's members are its own properties, so re-pointing its prototype at
    /// <c>new.target.prototype</c> adds the class's methods without displacing any of them, and the
    /// class chain already ends at <c>HTMLElement.prototype</c> — which is genuinely in the element's
    /// chain since the interface prototypes were linked.
    /// </para>
    /// </remarks>
    private void RegisterCustomElements(JSContext context, JSObject window)
    {
        var registry = new JSObject();
        registry.FastAddValue("define",
            new DomFunction((in a) => CustomElements.Define(in a), "define", 2),
            JSPropertyAttributes.EnumerableConfigurableValue);
        registry.FastAddValue("get",
            new DomFunction((in a) => CustomElements.Get(in a), "get", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);
        registry.FastAddValue("getName",
            new DomFunction((in a) => CustomElements.GetName(in a), "getName", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);
        registry.FastAddValue("whenDefined",
            new DomFunction((in a) => CustomElements.WhenDefined(in a), "whenDefined", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);
        registry.FastAddValue("upgrade",
            new DomFunction((in a) => CustomElements.Upgrade(in a), "upgrade", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        // The host half of the base, reached only from the JavaScript below. Named with the bridge's
        // reserved prefix and deleted from the global once the base has closed over it, so a page
        // cannot call it and mint an element out of band.
        context["__broilerConstructCustomElement"] =
            new DomFunction((in a) => CustomElements.ConstructForNewTarget(in a), "constructCustomElement", 2);
        context["__broilerCustomElementRegistry"] = registry;

        context.Eval("""
            (function () {
                var construct = __broilerConstructCustomElement;
                var registry = __broilerCustomElementRegistry;

                // The per-tag interface globals were registered before this pass and left their
                // construction hook unbound, because constructing is this registry's job: a
                // customized built-in reaches HTMLButtonElement (or whichever) through super(), and
                // the element it must produce is one of ours. Binding it here, once, is what makes
                // `class Fancy extends HTMLButtonElement` work; the setter deletes itself.
                if (typeof __broilerBindInterfaceConstructor === 'function')
                    __broilerBindInterfaceConstructor(construct);

                // The custom element base. Replaces the illegal-constructor HTMLElement, which is
                // still what a bare `new HTMLElement()` gets: without a new.target there is no
                // subclass to build, and the host answers that with a TypeError.
                function HTMLElement() {
                    var target = new.target;
                    if (!target) throw new TypeError('Illegal constructor');
                    // The host answers null for a new.target with no definition — a bare
                    // `new HTMLElement()`, whose new.target is HTMLElement itself, and any subclass
                    // that was never registered — and a string when the definition exists but names
                    // a different interface, which is a customized built-in reached through
                    // HTMLElement. Throwing here rather than there is what makes either a real
                    // TypeError with a name and a message: a host throw would surface as a bare
                    // string with neither.
                    var element = construct(target, 'HTMLElement');
                    if (typeof element === 'string') throw new TypeError(element);
                    if (!element) throw new TypeError('Illegal constructor');
                    // The element carries its DOM members as own properties, so re-pointing the
                    // prototype adds the class without taking anything away.
                    Object.setPrototypeOf(element, target.prototype);
                    return element;
                }

                // Keep the prototype the interface linking already built, so the element's chain
                // still reaches Element, Node and EventTarget through it and every wrapper linked to
                // HTMLElement.prototype keeps the object it was linked to.
                HTMLElement.prototype = __broilerHTMLElementPrototype;
                Object.defineProperty(HTMLElement.prototype, 'constructor', {
                    value: HTMLElement, writable: true, enumerable: false, configurable: true
                });
                globalThis.HTMLElement = HTMLElement;

                function CustomElementRegistry() { throw new TypeError('Illegal constructor'); }
                globalThis.CustomElementRegistry = CustomElementRegistry;
                Object.setPrototypeOf(registry, CustomElementRegistry.prototype);
                globalThis.customElements = registry;

                delete globalThis.__broilerConstructCustomElement;
                delete globalThis.__broilerCustomElementRegistry;
            })();
            """);

        window.FastAddValue("customElements", registry, JSPropertyAttributes.EnumerableConfigurableValue);
        SubscribeCustomElementReactions();
    }

    /// <summary>
    /// Turns the canonical mutation stream into reaction callbacks.
    /// </summary>
    /// <remarks>
    /// <c>DomDocument.Mutated</c> is raised synchronously at mutation time, which is what this needs:
    /// a browser runs <c>connectedCallback</c> before the statement after the <c>appendChild</c> that
    /// caused it. Building reactions on <c>MutationObserver</c> instead — the obvious reuse, since it
    /// already subscribes here — would have delivered every one of them a microtask late, and a
    /// component that reads its own DOM straight after inserting itself would have seen nothing.
    /// </remarks>
    private void SubscribeCustomElementReactions()
    {
        if (_customElementReactionsSubscribed)
            return;

        _customElementReactionsSubscribed = true;
        _document.Mutated += OnCustomElementRelevantMutation;
        foreach (var other in _browsingContextDocuments)
            other.Mutated += OnCustomElementRelevantMutation;
    }

    /// <summary>
    /// Subscribes a document this bridge minted for a detached browsing context, so a node adopted
    /// <em>into</em> it is heard.
    /// </summary>
    /// <remarks>
    /// Adoption publishes its record on the document the node is moving to, not the one it is leaving
    /// — so listening to the main document alone hears an adoption into the page and misses the
    /// symmetric one out of it. There is one registry across every document this bridge owns, so the
    /// same handler serves them all.
    /// </remarks>
    private void SubscribeBrowsingContextDocument(DomDocument document)
    {
        if (!_browsingContextDocuments.Add(document) || !_customElementReactionsSubscribed)
            return;

        document.Mutated += OnCustomElementRelevantMutation;
    }

    private readonly HashSet<DomDocument> _browsingContextDocuments = [];

    private bool _customElementReactionsSubscribed;

    private void OnCustomElementRelevantMutation(DomMutationRecord record)
    {
        if (_customElements is not { } registry)
            return;

        if (record.Type == DomMutationType.Adoption)
        {
            registry.OnAdoption(record.Target, DocumentValue(record.OldDocument), DocumentValue(record.NewDocument));
            return;
        }

        if (record.Type == DomMutationType.ChildList)
        {
            // Both lists are optional on the record, and a childList record normally carries only
            // one of them.
            registry.OnChildListMutation(
                record.AddedNodes ?? [],
                record.RemovedNodes ?? []);
            // A move changes a form-associated custom element's owner and can change its disabled
            // state (an ancestor fieldset), and both are computed from the tree rather than stored.
            registry.SyncFormState();
            return;
        }

        if (record.Type == DomMutationType.Attributes && record.Target is DomElement element &&
            record.AttributeName is { } attributeName)
        {
            registry.OnAttributeMutation(element, attributeName, record.OldValue);
            // `disabled`, `form` and `id` each move a form-associated custom element between states;
            // the sweep costs one pass over the elements a page actually upgraded.
            registry.SyncFormState();
        }
    }

    /// <summary>The JS object for a document node — the window's <c>document</c> for the page, its own
    /// wrapper for a document this bridge minted, and <c>null</c> for one that has neither.</summary>
    private JSValue DocumentValue(DomDocument? document)
    {
        if (document is null)
            return JavaScript.BuiltIns.Null.JSNull.Value;

        if (ReferenceEquals(document, _document))
            return _documentJSObject ?? (JSValue)JavaScript.BuiltIns.Null.JSNull.Value;

        return _jsObjects.TryGetDocument(document, out var wrapper)
            ? wrapper
            : JavaScript.BuiltIns.Null.JSNull.Value;
    }
}
