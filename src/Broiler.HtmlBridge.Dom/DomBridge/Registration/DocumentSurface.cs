using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

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
    /// <para>
    /// The cached local is a <see cref="JsValue"/> and the accessor is the realm's: the module's
    /// collection builders are JSEAL's, and a handle is what "built once and closed over" now holds.
    /// <see cref="JsValue.Missing"/> is the not-yet-built state rather than a nullable, because a
    /// built collection is always an object and Missing is a kind no builder can answer with.
    /// </para>
    /// </remarks>
    private void RegisterDocumentCollections(JsValue document)
    {
        var realm = Realm;

        Live("forms", Dom.Features.DocumentCollectionBinding.Forms);
        Live("images", Dom.Features.DocumentCollectionBinding.Images);
        Live("links", Dom.Features.DocumentCollectionBinding.Links);
        Live("anchors", Dom.Features.DocumentCollectionBinding.Anchors);
        Live("scripts", Dom.Features.DocumentCollectionBinding.Scripts);
        Live("styleSheets", Dom.Features.DocumentCollectionBinding.StyleSheets);

        // embeds and plugins are one collection under two names, not two collections that agree.
        var embeds = JsValue.Missing;
        JsValue Embeds()
        {
            if (embeds.IsMissing)
                embeds = Dom.Features.DocumentCollectionBinding.Embeds(this);
            return embeds;
        }

        Getter("embeds", Embeds);
        Getter("plugins", Embeds);

        void Live(string name, Func<Dom.Features.IDocumentCollectionHost, JsValue> build)
        {
            var collection = JsValue.Missing;
            Getter(name, () =>
            {
                if (collection.IsMissing)
                    collection = build(this);
                return collection;
            });
        }

        void Getter(string name, Func<JsValue> read) =>
            realm.DefineAccessor(document, name, (in _) => read(), null);
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
    private void RegisterDocumentMetadata(JsValue document)
    {
        var realm = Realm;

        realm.DefineAccessor(
            document,
            "doctype",
            (in _) => DocumentTypeNode() is { } doctype ? WrapNode(doctype) : JsValue.Null,
            null);

        // HTML §3.2.6: `dir` reflects the document element's dir attribute *limited to only known
        // values* — the getter answers the canonical lower-case keyword or the empty string, while
        // the setter writes through unchanged. So `document.dir = 'LTR'` reads back as "ltr" with
        // the attribute still spelled "LTR", and an unknown value reads back as "" with the
        // attribute set to whatever was assigned.
        //
        // The setter's coercion is the realm's ToJsString, not the handle's diagnostic rendering:
        // `document.dir = {toString(){return 'rtl'}}` is entitled to run that toString, which is what
        // the engine's own value-to-string did here before.
        realm.DefineAccessor(
            document,
            "dir",
            (in _) => JsValue.String(DocumentDirection()),
            (in c) =>
            {
                SetAttr(DocumentElement, "dir", c.Length > 0 ? c.Realm.ToJsString(c[0]) : string.Empty);
                return JsValue.Undefined;
            });

        // HTML §3.2.7: an enumerated document state, not an attribute, so it lives on the bridge.
        // Assigning anything but "on"/"off" (ASCII case-insensitively) is ignored rather than
        // stored — `document.designMode = 'zzz'` leaves the previous value in place.
        realm.DefineAccessor(
            document,
            "designMode",
            (in _) => JsValue.String(_designMode),
            (in c) =>
            {
                var requested = c.Length > 0 ? c.Realm.ToJsString(c[0]) : string.Empty;
                if (string.Equals(requested, "on", StringComparison.OrdinalIgnoreCase))
                    _designMode = "on";
                else if (string.Equals(requested, "off", StringComparison.OrdinalIgnoreCase))
                    _designMode = "off";
                return JsValue.Undefined;
            });
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
