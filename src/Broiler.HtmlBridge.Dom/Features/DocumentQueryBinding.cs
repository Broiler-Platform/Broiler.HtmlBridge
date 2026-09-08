using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The <c>document</c> element-query methods — <c>getElementById</c>, <c>getElementsByTagName</c>,
/// <c>getElementsByClassName</c>, <c>querySelector</c>, <c>querySelectorAll</c> — co-located as an
/// HtmlBridge feature module (Phase 3). Each searches the document tree and returns the matching
/// element's JS wrapper (or a live collection of wrappers). The document root, element list, wrapper
/// factory, selector validation and the collection factories are reached through the narrow
/// <see cref="IDocumentQueryHost"/> contract; sub-tree search (<c>FindInSubTree</c>) and attribute
/// reads (<c>TryGetAttribute</c>) are the bridge's neutral <c>internal static</c> helpers, called
/// directly. Previously the bridge's <c>JsRegistrationGetElementById006Core</c> etc. in the shared
/// JsFunctionCallbacks/Registration.cs grab-bag. Hit-testing
/// (<c>elementFromPoint</c>/<c>elementsFromPoint</c>), the structural accessors
/// (<c>body</c>/<c>head</c>/<c>title</c>) and the live collections
/// (<c>forms</c>/<c>images</c>/<c>links</c>/<c>styleSheets</c>) are separate concerns, not part of
/// this slice.
/// </summary>
/// <remarks>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>), so nothing here names an engine
/// type. Every argument read goes through the realm's <c>ToString</c> rather than the handle's,
/// because that is the coercion a page observes: <c>getElementById({toString(){…}})</c> has always
/// run the object's own <c>toString</c>, and the handle's rendering deliberately does not.
/// </remarks>
internal static class DocumentQueryBinding
{
    public static JsValue GetElementById(IDocumentQueryHost host, in JsCall call)
    {
        var id = call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty;
        var found = DomBridge.FindInSubTree(host.DocumentElement, el => el.Id == id);
        return found != null ? host.ToJsObject(found) : JsValue.Null;
    }

    /// <summary>
    /// <c>document.getElementsByTagName(name)</c> — a <b>live</b> <c>HTMLCollection</c> (DOM §4.5).
    /// </summary>
    public static JsValue GetElementsByTagName(IDocumentQueryHost host, in JsCall call)
    {
        var tag = call.Length > 0 ? call.Realm.ToJsString(call[0]).ToLowerInvariant() : string.Empty;

        // The realm is read out of the call frame here rather than inside the contents function: the
        // collection outlives this call, and a ref struct cannot be captured by the closure anyway.
        var realm = call.Realm;
        return LiveCollection(host, realm, () =>
        {
            var results = new List<JsValue>();
            foreach (var el in host.Elements)
            {
                if (tag == "*" || el.TagName == tag)
                    results.Add(host.ToJsObject(el));
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
    public static JsValue GetElementsByClassName(IDocumentQueryHost host, in JsCall call)
    {
        var wanted = ClassNameSet.Parse(call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty);
        var realm = call.Realm;
        return LiveCollection(host, realm, () =>
        {
            var results = new List<JsValue>();
            foreach (var el in host.Elements)
            {
                if (ClassNameSet.Matches(el, wanted))
                    results.Add(host.ToJsObject(el));
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
    public static JsValue GetElementsByName(IDocumentQueryHost host, in JsCall call)
    {
        var name = call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty;

        // A live NodeList, not an HTMLCollection: HTML §3.1.5 is the one by-name lookup the
        // specification types as a NodeList.
        return host.NodeList(() =>
        {
            var results = new List<JsValue>();
            foreach (var el in host.Elements)
            {
                if (DomBridge.TryGetAttribute(el, "name", out var value) && string.Equals(value, name, StringComparison.Ordinal))
                    results.Add(host.ToJsObject(el));
            }

            return results;
        });
    }

    public static JsValue QuerySelector(IDocumentQueryHost host, in JsCall call)
    {
        var selector = call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty;
        host.ValidateSelector(selector);
        if (DomApiSyntax.CarriesPseudoElement(selector))
            return JsValue.Null;

        foreach (var el in host.Elements)
        {
            if (host.MatchesSelector(el, selector))
                return host.ToJsObject(el);
        }

        return JsValue.Null;
    }

    /// <summary>
    /// <c>document.querySelectorAll(selector)</c> — a <b>static</b> <c>NodeList</c> (DOM §4.2.6),
    /// the one collection the specification defines as a snapshot rather than live.
    /// </summary>
    public static JsValue QuerySelectorAll(IDocumentQueryHost host, in JsCall call)
    {
        var selector = call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty;
        host.ValidateSelector(selector);
        var results = new List<JsValue>();
        if (!DomApiSyntax.CarriesPseudoElement(selector))
        {
            foreach (var el in host.Elements)
            {
                if (host.MatchesSelector(el, selector))
                    results.Add(host.ToJsObject(el));
            }
        }

        // The list is computed once and closed over, which is what makes this collection static.
        return host.NodeList(() => results);
    }

    /// <summary>An <c>HTMLCollection</c> over <paramref name="contents"/>, with the named getter
    /// DOM §4.2.10.2 gives one — by <c>id</c>, then by <c>name</c>.</summary>
    private static JsValue LiveCollection(IDocumentQueryHost host, IJsRealm realm, Func<List<JsValue>> contents) =>
        host.HtmlCollection(contents, name =>
        {
            if (name.Length == 0)
                return null;

            foreach (var candidate in contents())
            {
                if (candidate.IsObject &&
                    (Named(realm, candidate, "id", name) || Named(realm, candidate, "name", name)))
                    return candidate;
            }

            return null;
        });

    /// <summary>
    /// Whether the wrapper's <paramref name="attribute"/> property is the string
    /// <paramref name="name"/>.
    /// </summary>
    /// <remarks>
    /// The property has to <em>be</em> a string, not be coercible to one — a wrapper whose <c>id</c>
    /// is absent reads <c>undefined</c> and must not match the name "undefined". That is what the
    /// engine-typed <c>is JSString</c> test said, and <see cref="JsValue.IsString"/> says it without
    /// entering the engine. <see cref="JsValue.AsString"/> likewise does not coerce, so reading the
    /// property of one collection member cannot run a <c>toString</c> the page wrote.
    /// </remarks>
    private static bool Named(IJsRealm realm, JsValue wrapper, string attribute, string name)
    {
        var value = realm.GetProperty(wrapper, attribute);
        return value.IsString && string.Equals(value.AsString, name, StringComparison.Ordinal);
    }
}
