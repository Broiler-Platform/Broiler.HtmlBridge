using Broiler.Dom;
using Broiler.JavaScript.Engine;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.BuiltIns.Null;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The <c>document</c> collection accessors — <c>forms</c>, <c>images</c>, <c>links</c>,
/// <c>anchors</c>, <c>scripts</c>, <c>embeds</c>, <c>plugins</c> and <c>styleSheets</c> — plus
/// <c>document.currentScript</c>, which names one element out of the same set, co-located as an
/// HtmlBridge feature module (Phase 3).
/// </summary>
/// <remarks>
/// <para>
/// Each of the element collections is an <c>HTMLCollection</c> over a filter of the document's
/// element list, which <see cref="IDocumentCollectionHost.Elements"/> recomputes from the tree on
/// every read — so the collection is live, as HTML §3.1.5 requires, and named access
/// (<c>document.forms.login</c>, <c>document.forms.namedItem('login')</c>) is the interface's own
/// named getter rather than properties copied onto a snapshot.
/// </para>
/// <para>
/// They used to be <see cref="JavaScript.BuiltIns.Array.JSArray"/>s built fresh per read, which was
/// wrong three ways at once. An array is not live, so a page that held <c>document.forms</c> across a
/// <c>appendChild</c> read a stale length; it has <c>map</c> and <c>filter</c> but no <c>item</c> or
/// <c>namedItem</c>, the opposite of a browser in both directions; and a fresh object per read made
/// <c>document.forms === document.forms</c> <see langword="false"/>, where every browser hands back
/// one cached object per document. The last one is why the collections are built once at
/// registration and closed over: identity is part of the contract, and
/// <c>document.plugins === document.embeds</c> is required by HTML §3.1.5 outright — the two names
/// are specified to return the same object, not merely equal ones.
/// </para>
/// <para>
/// <c>anchors</c>, <c>embeds</c> and <c>plugins</c> did not exist at all, so each read was
/// <c>undefined</c> and the idiomatic <c>document.embeds.length</c> a <c>TypeError</c>.
/// </para>
/// </remarks>
internal static class DocumentCollectionBinding
{
    /// <summary>Every <c>&lt;form&gt;</c> in the document, in tree order.</summary>
    public static JSValue Forms(IDocumentCollectionHost host, JSContext? context) =>
        Collection(host, context, static element => IsTag(element, "form"));

    /// <summary>Every <c>&lt;img&gt;</c> in the document, in tree order.</summary>
    public static JSValue Images(IDocumentCollectionHost host, JSContext? context) =>
        Collection(host, context, static element => IsTag(element, "img"));

    /// <summary>
    /// Every <c>&lt;a&gt;</c> and <c>&lt;area&gt;</c> that <em>has an <c>href</c></em> (HTML §3.1.5) —
    /// an anchor without one is not a link and is not in this collection.
    /// </summary>
    public static JSValue Links(IDocumentCollectionHost host, JSContext? context) =>
        Collection(host, context, static element =>
            (IsTag(element, "a") || IsTag(element, "area")) && DomBridge.HasAttr(element, "href"));

    /// <summary>
    /// Every <c>&lt;a&gt;</c> that has a <c>name</c> — the other half of the historical anchor split,
    /// and deliberately not the same set as <see cref="Links"/>: an <c>&lt;a name&gt;</c> with no
    /// <c>href</c> is in <c>anchors</c> and not in <c>links</c>, and an <c>&lt;a href&gt;</c> with no
    /// <c>name</c> is the reverse.
    /// </summary>
    public static JSValue Anchors(IDocumentCollectionHost host, JSContext? context) =>
        Collection(host, context, static element => IsTag(element, "a") && DomBridge.HasAttr(element, "name"));

    /// <summary>
    /// <c>document.scripts</c> — every <c>&lt;script&gt;</c> element in tree order.
    /// </summary>
    /// <remarks>
    /// The position within the collection is the property's entire purpose. The idiom that wants it
    /// is <c>document.currentScript || document.scripts[document.scripts.length - 1]</c> — how a
    /// classic script finds its own element while it runs, since during synchronous execution it is
    /// the last one in the document. Cloudflare's <c>email-decode.min.js</c> ends with exactly that
    /// line, and it is on every page Cloudflare serves with email obfuscation enabled, so the
    /// property being absent threw a TypeError out of a script the page never asked for. It was
    /// reported against html5test.com; nothing about that site is special.
    /// </remarks>
    public static JSValue Scripts(IDocumentCollectionHost host, JSContext? context) =>
        Collection(host, context, static element => IsTag(element, "script"));

    /// <summary>
    /// Every <c>&lt;embed&gt;</c> in the document. <c>document.plugins</c> is the same object, not a
    /// second one over the same filter — see the remarks on this class.
    /// </summary>
    public static JSValue Embeds(IDocumentCollectionHost host, JSContext? context) =>
        Collection(host, context, static element => IsTag(element, "embed"));

    /// <summary>
    /// <c>document.currentScript</c> — the <c>&lt;script&gt;</c> element whose classic script is
    /// executing, and <c>null</c> whenever none is (between scripts, inside a callback, in a
    /// module).
    /// </summary>
    /// <remarks>
    /// The property was absent, so reading it yielded <c>undefined</c> rather than <c>null</c>, and
    /// the difference is not cosmetic: <c>null</c> is a value a script can go on to test, while the
    /// idiomatic use is to dereference it immediately. Google's own tag-manager loader — served on
    /// <c>about.google</c>, which is where <c>google.com</c>'s "About" link leads — opens with
    /// <code>new URL(document.currentScript.src).searchParams</code>
    /// on its 14th line, so the missing property was a <c>TypeError</c> ("Cannot get property src of
    /// undefined") four lines into the page's first script. The next two lines are that script's
    /// <c>const id</c> and <c>const cookieCategory</c>, so aborting there also left the cookie bar's callback
    /// reading <c>id</c> in its temporal dead zone, and the page's analytics never initialised.
    /// <c>document.scripts[document.scripts.length - 1]</c>, the fallback the same idiom usually
    /// carries, does not help a script that spells the access without one.
    /// </remarks>
    public static JSValue GetCurrentScript(IDocumentCollectionHost host, in Arguments a)
    {
        var index = host.CurrentScriptIndex;
        if (index < 0 || index >= host.Elements.Count)
            return JSNull.Value;

        var element = host.Elements[index];
        return IsTag(element, "script") ? host.ToJSObject(element) : JSNull.Value;
    }

    /// <summary>
    /// <c>document.styleSheets</c> — a CSSOM <c>StyleSheetList</c> (§6.1) over every sheet associated
    /// with the document, which is a <c>&lt;style&gt;</c> <em>and</em> a
    /// <c>&lt;link rel=stylesheet&gt;</c>. <see cref="IDocumentCollectionHost.Elements"/> is already
    /// in document order, which is the order the collection is defined in.
    /// </summary>
    public static JSValue StyleSheets(IDocumentCollectionHost host, JSContext? context) =>
        DomCollectionBinding.StyleSheetList(context, () =>
        {
            var sheets = new List<JSValue>();
            foreach (var element in host.Elements)
            {
                if (host.HasAssociatedStyleSheet(element))
                    sheets.Add(host.BuildStyleSheetObject(element));
            }
            return sheets;
        });

    private static JSValue Collection(
        IDocumentCollectionHost host, JSContext? context, Func<DomElement, bool> predicate)
    {
        List<DomElement> Members()
        {
            var members = new List<DomElement>();
            foreach (var element in host.Elements)
            {
                if (predicate(element))
                    members.Add(element);
            }
            return members;
        }

        return DomCollectionBinding.HtmlCollection(
            context,
            () =>
            {
                var wrappers = new List<JSValue>();
                foreach (var member in Members())
                    wrappers.Add(host.ToJSObject(member));
                return wrappers;
            },
            name => NamedItem(host, Members(), name));
    }

    /// <summary>
    /// The <c>HTMLCollection</c> named getter (DOM §4.2.10.2): the first member, in tree order, whose
    /// <c>id</c> <em>or</em> <c>name</c> is <paramref name="name"/>.
    /// </summary>
    /// <remarks>
    /// One pass testing both attributes, rather than all ids and then all names, because the
    /// specification says "the first element for which at least one of the following is true" — so a
    /// member earlier in the tree carrying the name beats a later one carrying the id, and Chromium
    /// agrees: over <c>&lt;form id=b&gt;&lt;form name=a id=c&gt;&lt;form name=b&gt;</c>,
    /// <c>document.forms.b</c> is the first form, not the third.
    /// </remarks>
    private static JSValue? NamedItem(IDocumentCollectionHost host, List<DomElement> members, string name)
    {
        // The empty string is never a supported property name, however many members carry an empty
        // name attribute.
        if (name.Length == 0)
            return null;

        foreach (var member in members)
        {
            if ((DomBridge.TryGetAttribute(member, "id", out var id) && id == name) ||
                (DomBridge.TryGetAttribute(member, "name", out var named) && named == name))
            {
                return host.ToJSObject(member);
            }
        }

        return null;
    }

    private static bool IsTag(DomElement element, string tag) =>
        string.Equals(element.TagName, tag, StringComparison.OrdinalIgnoreCase);
}
