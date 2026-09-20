using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Broiler.Dom;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.JSeal;
using Broiler.HtmlBridge.Logging;
using Broiler.HtmlBridge.Scripting;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

/// <summary>
/// Sibling partial peeled out of <c>SubDocuments.cs</c> to keep it
/// under the 750-line guideline: XML/XHTML/SVG sub-document construction and sub-document script
/// execution. Builds a canonical <see cref="DomDocument"/> tree from XML content
/// (<see cref="BuildSubDocumentFromXml"/> / <see cref="BuildDomElementFromXElement"/>), and — for
/// correctly-namespaced XHTML — collects and runs embedded <c>&lt;script&gt;</c> content in the
/// main JS context. Pure partial-class relocation — no signature, accessibility, or logic change.
/// </summary>
public sealed partial class DomBridge
{
    /// <summary>
    /// Builds a sub-document tree from XML/SVG/XHTML content using an XML parser.
    /// For XHTML with valid namespace, also executes embedded scripts.
    /// XML well-formedness errors result in an empty document.
    /// </summary>
    private DomDocument BuildSubDocumentFromXml(
        string xmlContent,
        string contentType,
        DomElement containerElement,
        ContentSecurityPolicy? deliveredPolicy = null)
    {
        var document = CreateBrowsingContextDocument();

        try
        {
            // Strip XML processing instructions before parsing (XDocument doesn't need them)
            var cleanXml = xmlContent;
            while (cleanXml.TrimStart().StartsWith("<?xml-stylesheet", StringComparison.OrdinalIgnoreCase))
            {
                var piEnd = cleanXml.IndexOf("?>", StringComparison.Ordinal);
                if (piEnd >= 0) cleanXml = cleanXml[(piEnd + 2)..].TrimStart();
                else break;
            }

            var xdoc = XDocument.Parse(cleanXml);
            if (xdoc.Root == null)
            {
                LinkContentDocument(containerElement, document);
                return document;
            }

            // Check XHTML namespace validity
            var rootNs = xdoc.Root.Name.NamespaceName;
            var isXhtml = string.Equals(contentType, "application/xhtml+xml", StringComparison.OrdinalIgnoreCase);
            var hasCorrectXhtmlNs = string.Equals(rootNs, "http://www.w3.org/1999/xhtml", StringComparison.Ordinal);

            if (isXhtml && !hasCorrectXhtmlNs)
            {
                // Wrong XHTML namespace — create empty doc, don't execute scripts. It links
                // the container to its own document.
                return BuildEmptySubDocument(containerElement);
            }

            // Build DOM tree from XML
            var rootEl = BuildDomElementFromXElement(xdoc.Root);
            document.AppendChild(rootEl);

            LinkContentDocument(containerElement, document);

            // Execute scripts in XHTML documents with correct namespace
            if (isXhtml && hasCorrectXhtmlNs)
            {
                ExecuteSubDocumentScripts(
                    rootEl,
                    new ContentSecurityPolicySet(deliveredPolicy, ContentSecurityPolicy.FromHtml(xmlContent)));
            }
        }
        catch (System.Xml.XmlException)
        {
            // XML well-formedness error — return empty document, don't execute scripts
            LinkContentDocument(containerElement, document);
        }

        return document;
    }

    /// <summary>
    /// Recursively builds a Broiler.Dom.DomElement tree from an XElement.
    /// </summary>
    private DomElement BuildDomElementFromXElement(XElement xe)
    {
        var tagName = xe.Name.LocalName.ToLowerInvariant();
        var el = CreateBridgeElement(tagName);

        foreach (var attr in xe.Attributes())
        {
            if (!attr.IsNamespaceDeclaration)
                SetAttr(el, attr.Name.LocalName, attr.Value);
        }

        foreach (var child in xe.Nodes())
        {
            if (child is XElement childXe)
            {
                var childEl = BuildDomElementFromXElement(childXe);
                SetParent(childEl, el);
                el.AppendChild(childEl);
            }
            else if (child is XText childText)
            {
                var textNode = CreateBridgeTextNode(childText.Value);
                SetParent(textNode, el);
                el.AppendChild(textNode);
            }
        }

        return el;
    }

    /// <summary>
    /// Finds and executes script elements within a sub-document tree.
    /// Scripts call parent.notify() etc. in the main JS context.
    /// </summary>
    /// <param name="policies">
    /// The Content-Security-Policies enforced for this sub-document: the first one its own markup
    /// declares, plus the one delivered to it — its embedder's when the frame has a local scheme and inherits
    /// it, or its response header's when it came off the network. Each script runs only if all of
    /// them admit it.
    /// </param>
    /// <remarks>
    /// <b>This path consulted no policy at all before, which is a different failure from consulting
    /// one that turned out to be null.</b> The HTML path at least asked, and got <c>null</c> for a
    /// frame that declared nothing; here nothing was ever looked for, so an XHTML frame ran its
    /// scripts under any policy whatever. <c>SubDocumentContentSecurityPolicyTests</c> pins it, with
    /// the control that says the same document still runs when nothing forbids it — without that
    /// control the test would pass on a path where scripts never run.
    /// </remarks>
    private void ExecuteSubDocumentScripts(DomElement docRoot, ContentSecurityPolicySet policies = default)
    {
        if (_realm is not { } realm) return;

        var scripts = new List<string>();
        CollectScriptContent(docRoot, scripts);

        var ordinal = 0;

        foreach (var scriptCode in scripts)
        {
            // An XML sub-document's scripts are inline by construction -- CollectScriptContent takes
            // an element's text and never a src -- so the inline directive is the one that decides.
            if (!policies.AllowsInlineScript(scriptText: scriptCode))
                continue;

            try
            {
                // A CLASSIC SCRIPT, exactly as the HTML path's are, and evaluated through the same
                // member for the same reason: script-src governs a script element, the decision was
                // taken on the line above, and 'unsafe-eval' has nothing to say about either.
                //
                // The ordinal counts scripts ADMITTED rather than scripts found, so a label names the
                // n-th script that ran and not the n-th that was looked at. The two differ exactly
                // when a policy refused one, which is the moment a reader is most likely to be
                // reading these labels.
                realm.EvaluateClassicScript(scriptCode, $"subdocument:xml:{ordinal++}");
            }
            catch (Exception ex)
            {
                RenderLogger.LogWarning(LogCategory.JavaScript, "DomBridge.ExecuteSubDocumentScripts",
                    $"Sub-document script error: {ex.Message}", ex);
            }
        }
    }
}


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

/// <summary>
/// The top layer inside a nested browsing context — a modal <c>&lt;dialog&gt;</c> or an open
/// popover in a frame.
/// <para>
/// The renderer's UA sheet has no <c>dialog:modal</c> rule; the bridge supplies it, baking
/// <c>position: fixed</c>, the <c>inset: 0</c>/<c>margin: auto</c> centring, the top-layer marker
/// and the <c>::backdrop</c> scrim onto the element before serialization. That pass walked
/// <see cref="DomBridge.Elements"/> and the main document's tree, and a frame's document is severed
/// from both — so a modal dialog inside a frame got none of it. It laid out as an ordinary
/// absolutely-positioned box in its <c>&lt;body&gt;</c> and painted with no scrim behind it: in WPT
/// <c>css-view-transitions/dialog-in-rtl-iframe</c> the dialog landed in the frame's top corner
/// (right, the document being <c>dir=rtl</c>) instead of centred, over white instead of the
/// backdrop's grey.
/// </para>
/// <para>
/// Each frame is its own document, and everything the passes read is already per-document — the
/// cascade is resolved by that document's own style scope
/// (<see cref="DomBridge.GetSyncedScopedEngine"/>), and centring depends only on what the author
/// declared, not on resolved pixels. So the same passes are simply run again, once per frame.
/// </para>
/// </summary>
public sealed partial class DomBridge
{
    /// <summary>
    /// Applies the top-layer passes to every nested browsing context: the UA modal-dialog and
    /// popover positioning, then the <c>::backdrop</c> each generates.
    /// </summary>
    /// <remarks>
    /// The anchor-positioning machinery around these passes in the main document
    /// (<c>anchor()</c>, <c>position-area</c>, <c>position-try</c>) is not run here: it resolves
    /// against geometry this bridge only measures for the main frame. What is run needs no
    /// geometry. A frame's backdrop therefore takes the native marker path, and an author
    /// <c>::backdrop</c> with <c>position-try-fallbacks</c> — which needs the synthesized
    /// <c>&lt;div&gt;</c> and the viewport it is sized against — falls back to the frame's own
    /// viewport rather than the main one.
    /// </remarks>
    private void ApplySubDocumentTopLayer(
        Dictionary<string, AnchorInfo> anchorRegistry,
        Dictionary<string, IReadOnlyDictionary<string, string>> positionTryRules)
    {
        // Snapshot: a pass below can materialise a frame (reading a computed style loads a
        // sub-document), which would otherwise mutate the map mid-iteration.
        foreach (var contentDocument in _browsingContexts.ContentDocuments.ToList())
        {
            if (GetDocumentElement(contentDocument) is not { } subRoot)
                continue;

            var elements = subRoot.InclusiveDescendants().OfType<DomElement>().ToList();
            ApplyDialogUAPositioning(elements);
            ApplyPopoverUAPositioning(elements);

            var (frameWidth, frameHeight) = GetViewportForDocRoot(subRoot);
            InsertDialogBackdrops(subRoot, frameWidth, frameHeight, anchorRegistry, positionTryRules);
        }
    }
}
