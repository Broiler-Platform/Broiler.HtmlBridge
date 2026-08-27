using System;
using System.Collections.Generic;
using System.Linq;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Storage;
using Broiler.JavaScript.BuiltIns.Array;
using Broiler.JavaScript.BuiltIns.Null;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The <c>document</c> element-query methods — <c>getElementById</c>, <c>getElementsByTagName</c>,
/// <c>getElementsByClassName</c>, <c>querySelector</c>, <c>querySelectorAll</c> — co-located as an
/// HtmlBridge feature module (Phase 3). Each searches the document tree and returns the matching
/// element's JS wrapper (or a live-array of wrappers). The document root, element list and wrapper
/// factory are reached through the narrow <see cref="IDocumentQueryHost"/> contract; sub-tree search
/// (<c>FindInSubTree</c>) and selector matching (<c>MatchesSelector</c>) are the bridge's neutral
/// <c>internal static</c> helpers, called directly. Previously the bridge's
/// <c>JsRegistrationGetElementById006Core</c> etc. in the shared JsFunctionCallbacks/Registration.cs
/// grab-bag. Hit-testing (<c>elementFromPoint</c>/<c>elementsFromPoint</c>), the structural
/// accessors (<c>body</c>/<c>head</c>/<c>title</c>) and the live collections
/// (<c>forms</c>/<c>images</c>/<c>links</c>/<c>styleSheets</c>) are separate concerns, not part of
/// this slice.
/// </summary>
internal static class DocumentQueryBinding
{
    public static JSValue GetElementById(IDocumentQueryHost host, in Arguments a)
    {
        var id = a.Length > 0 ? a[0].ToString() : string.Empty;
        var found = DomBridge.FindInSubTree(host.DocumentElement, el => el.Id == id);
        return found != null ? host.ToJSObject(found) : JSNull.Value;
    }

    /// <summary>
    /// <c>document.getElementsByTagName(name)</c> — a <b>live</b> <c>HTMLCollection</c> (DOM §4.5).
    /// </summary>
    public static JSValue GetElementsByTagName(IDocumentQueryHost host, in Arguments a)
    {
        var tag = a.Length > 0 ? a[0].ToString().ToLowerInvariant() : string.Empty;
        return LiveCollection(host, () =>
        {
            var results = new List<JSValue>();
            foreach (var el in host.Elements)
            {
                if (tag == "*" || el.TagName == tag)
                    results.Add(host.ToJSObject(el));
            }

            return results;
        });
    }

    /// <summary>
    /// <c>document.getElementsByClassName(names)</c>. The argument is an ordered set of class names
    /// and an element must carry every one of them (DOM §4.5) — this read it as a single name, so a
    /// multi-class query such as <c>getElementsByClassName("a b")</c> matched only an element whose
    /// class attribute was that literal string, i.e. nothing. <see cref="ClassNameSet"/> holds the
    /// rule so this and the element half of the same method cannot answer differently.
    /// </summary>
    public static JSValue GetElementsByClassName(IDocumentQueryHost host, in Arguments a)
    {
        var wanted = ClassNameSet.Parse(a.Length > 0 ? a[0].ToString() : string.Empty);
        return LiveCollection(host, () =>
        {
            var results = new List<JSValue>();
            foreach (var el in host.Elements)
            {
                if (ClassNameSet.Matches(el, wanted))
                    results.Add(host.ToJSObject(el));
            }

            return results;
        });
    }

    /// <summary>
    /// <c>document.getElementsByName(name)</c> — HTML §3.1.5: every element in the document whose
    /// <c>name</c> <em>attribute</em> is identical to the argument, in tree order.
    /// </summary>
    /// <remarks>
    /// It is not a synonym for the id lookup and not confined to form controls: any element carrying
    /// the attribute qualifies, which is why it is an attribute read rather than a selector match.
    /// The value is compared ordinally — HTML matches it exactly — while the attribute's own name is
    /// matched case-insensitively, as attribute names are in an HTML document.
    /// <para>
    /// It was missing entirely, and on a document that reads as <see langword="undefined"/> rather
    /// than as an absent method, so calling it threw <c>TypeError: undefined is not a function</c> and
    /// took the whole script with it. google.com's homepage bundle finds its search form this way —
    /// <c>for(var d=0;b=c[d++];)if(b=document.getElementsByName(b)[0])return b</c> over
    /// <c>["f","gs"]</c> — after looking for a form by id first.
    /// </para>
    /// </remarks>
    public static JSValue GetElementsByName(IDocumentQueryHost host, in Arguments a)
    {
        var name = a.Length > 0 ? a[0].ToString() : string.Empty;
        // A live NodeList, not an HTMLCollection: HTML §3.1.5 is the one by-name lookup the
        // specification types as a NodeList.
        return DomCollectionBinding.NodeList(host.JsContext, () =>
        {
            var results = new List<JSValue>();
            foreach (var el in host.Elements)
            {
                if (DomBridge.TryGetAttribute(el, "name", out var value) && string.Equals(value, name, StringComparison.Ordinal))
                    results.Add(host.ToJSObject(el));
            }

            return results;
        });
    }

    public static JSValue QuerySelector(IDocumentQueryHost host, in Arguments a)
    {
        var selector = a.Length > 0 ? a[0].ToString() : string.Empty;
        DomBridge.ValidateSelector(selector, host.JsContext);
        if (DomApiSyntax.CarriesPseudoElement(selector))
            return JSNull.Value;

        foreach (var el in host.Elements)
        {
            if (host.MatchesSelector(el, selector))
                return host.ToJSObject(el);
        }

        return JSNull.Value;
    }

    /// <summary>
    /// <c>document.querySelectorAll(selector)</c> — a <b>static</b> <c>NodeList</c> (DOM §4.2.6),
    /// the one collection the specification defines as a snapshot rather than live.
    /// </summary>
    public static JSValue QuerySelectorAll(IDocumentQueryHost host, in Arguments a)
    {
        var selector = a.Length > 0 ? a[0].ToString() : string.Empty;
        DomBridge.ValidateSelector(selector, host.JsContext);
        var results = new List<JSValue>();
        if (!DomApiSyntax.CarriesPseudoElement(selector))
        {
            foreach (var el in host.Elements)
            {
                if (host.MatchesSelector(el, selector))
                    results.Add(host.ToJSObject(el));
            }
        }

        return DomCollectionBinding.NodeList(host.JsContext, () => results);
    }

    /// <summary>An <c>HTMLCollection</c> over <paramref name="contents"/>, with the named getter
    /// DOM §4.2.10.2 gives one — by <c>id</c>, then by <c>name</c>.</summary>
    private static JSValue LiveCollection(IDocumentQueryHost host, Func<List<JSValue>> contents) =>
        DomCollectionBinding.HtmlCollection(host.JsContext, contents, name =>
        {
            if (name.Length == 0)
                return null;

            foreach (var candidate in contents())
            {
                if (candidate is JSObject wrapper &&
                    (Named(wrapper, "id", name) || Named(wrapper, "name", name)))
                    return wrapper;
            }

            return null;
        });

    private static bool Named(JSObject wrapper, string attribute, string name) =>
        wrapper[(KeyString)attribute] is JSString value &&
        string.Equals(value.ToString(), name, StringComparison.Ordinal);
}
