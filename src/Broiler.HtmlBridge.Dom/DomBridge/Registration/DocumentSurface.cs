using Broiler.Dom;
using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.Engine;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;

namespace Broiler.HtmlBridge;

/// <summary>
/// The <c>document</c> surface that is neither a node operation nor a factory: its eight live
/// collections, and the three metadata accessors that were simply absent — <c>doctype</c>,
/// <c>dir</c> and <c>designMode</c>.
/// </summary>
public sealed partial class DomBridge
{
    /// <summary>
    /// Registers <c>forms</c>, <c>images</c>, <c>links</c>, <c>anchors</c>, <c>scripts</c>,
    /// <c>embeds</c>, <c>plugins</c> and <c>styleSheets</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each collection object is built <em>once</em> and closed over, so the getter hands back the
    /// same object on every read. That is the identity a browser guarantees
    /// (<c>document.forms === document.forms</c>), and for <c>plugins</c> it is the specification's
    /// literal requirement rather than a nicety: HTML §3.1.5 says <c>plugins</c> must return the same
    /// object <c>embeds</c> does, which one shared local expresses exactly.
    /// </para>
    /// <para>
    /// Built lazily on first read rather than here, because the interface constructors these
    /// collections take their prototypes from are registered later in the attach sequence (the
    /// polyfill pass, after the document object is populated). Constructing eagerly would leave every
    /// collection prototype-less and <c>document.forms instanceof HTMLCollection</c> false. A context
    /// is single-threaded by construction, so the null check needs no guard.
    /// </para>
    /// </remarks>
    private void RegisterDocumentCollections(JSContext context, JSObject document)
    {
        Live("forms", Dom.Features.DocumentCollectionBinding.Forms);
        Live("images", Dom.Features.DocumentCollectionBinding.Images);
        Live("links", Dom.Features.DocumentCollectionBinding.Links);
        Live("anchors", Dom.Features.DocumentCollectionBinding.Anchors);
        Live("scripts", Dom.Features.DocumentCollectionBinding.Scripts);
        Live("styleSheets", Dom.Features.DocumentCollectionBinding.StyleSheets);

        // embeds and plugins are one collection under two names, not two collections that agree.
        JSValue? embeds = null;
        JSValue Embeds() => embeds ??= Dom.Features.DocumentCollectionBinding.Embeds(this, context);
        Getter("embeds", Embeds);
        Getter("plugins", Embeds);

        void Live(string name, Func<Dom.Features.IDocumentCollectionHost, JSContext?, JSValue> build)
        {
            JSValue? collection = null;
            Getter(name, () => collection ??= build(this, context));
        }

        void Getter(string name, Func<JSValue> read) =>
            document.FastAddProperty(
                name, new DomFunction((in _) => read(), $"get {name}"), null,
                JSPropertyAttributes.EnumerableConfigurableProperty);
    }

    /// <summary>
    /// <c>document.doctype</c>, <c>document.dir</c> and <c>document.designMode</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>doctype</c> is the one of the three that was not merely unimplemented but <em>invisible</em>:
    /// the parser has produced a canonical <see cref="DomDocumentType"/> and appended it as the
    /// document's first child for some time, and <c>document.firstChild</c> already returned it — only
    /// the accessor DOM §4.5 names for it was missing, so the node was reachable by position and not
    /// by name.
    /// </para>
    /// </remarks>
    private void RegisterDocumentMetadata(JSObject document)
    {
        document.FastAddProperty(
            "doctype",
            new DomFunction((in _) => DocumentTypeNode() is { } doctype ? ToJSObject(doctype) : JSNull.Value, "get doctype"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // HTML §3.2.6: `dir` reflects the document element's dir attribute *limited to only known
        // values* — the getter answers the canonical lower-case keyword or the empty string, while
        // the setter writes through unchanged. So `document.dir = 'LTR'` reads back as "ltr" with
        // the attribute still spelled "LTR", and an unknown value reads back as "" with the
        // attribute set to whatever was assigned.
        document.FastAddProperty(
            "dir",
            new DomFunction((in _) => new JSString(DocumentDirection()), "get dir"),
            new DomFunction((in a) =>
            {
                SetAttr(DocumentElement, "dir", a.Length > 0 ? a[0].ToString() : string.Empty);
                return JSUndefined.Value;
            }, "set dir"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        // HTML §3.2.7: an enumerated document state, not an attribute, so it lives on the bridge.
        // Assigning anything but "on"/"off" (ASCII case-insensitively) is ignored rather than
        // stored — `document.designMode = 'zzz'` leaves the previous value in place.
        document.FastAddProperty(
            "designMode",
            new DomFunction((in _) => new JSString(_designMode), "get designMode"),
            new DomFunction((in a) =>
            {
                var requested = a.Length > 0 ? a[0].ToString() : string.Empty;
                if (string.Equals(requested, "on", StringComparison.OrdinalIgnoreCase))
                    _designMode = "on";
                else if (string.Equals(requested, "off", StringComparison.OrdinalIgnoreCase))
                    _designMode = "off";
                return JSUndefined.Value;
            }, "set designMode"),
            JSPropertyAttributes.EnumerableConfigurableProperty);
    }

    private string _designMode = "off";

    /// <summary>The document's <see cref="DomDocumentType"/> child, or <see langword="null"/>.</summary>
    private DomDocumentType? DocumentTypeNode()
    {
        foreach (var child in _document.ChildNodes)
        {
            if (child is DomDocumentType doctype)
                return doctype;
        }

        return null;
    }

    /// <summary>The document element's <c>dir</c>, limited to the three keywords HTML defines.</summary>
    private string DocumentDirection()
    {
        if (!TryGetAttribute(DocumentElement, "dir", out var value))
            return string.Empty;

        var keyword = value.ToLowerInvariant();
        return keyword is "ltr" or "rtl" or "auto" ? keyword : string.Empty;
    }
}
