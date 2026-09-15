using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    internal static JsValue? NamedItem(IJsRealm realm, Func<List<JsValue>> contents, string name)
    {
        if (name.Length == 0)
            return null;

        foreach (var candidate in contents())
        {
            if (candidate.IsObject &&
                (Matches(realm, candidate, "id", name) || Matches(realm, candidate, "name", name)))
                return candidate;
        }

        return null;

        // The attribute has to be a JavaScript *string* to match, exactly as before: an element whose
        // reflected `id` is anything else does not answer the named getter. So this reads the property
        // and tests its kind rather than coercing — realm.ToJsString would run a page toString and
        // make an object match a name it never had.
        static bool Matches(IJsRealm realm, JsValue wrapper, string attribute, string name) =>
            realm.GetProperty(wrapper, attribute) is { IsString: true } value &&
            string.Equals(value.AsString, name, StringComparison.Ordinal);
    }

    /// <summary>
    /// A selector result normalised to what the DOM says it can be: an object or JavaScript
    /// <c>null</c> and never anything else — a wrapper, a <c>NodeList</c>, an <c>HTMLCollection</c>,
    /// or the <c>null</c> a <c>querySelector</c> that matched nothing answers.
    /// </summary>
    /// <remarks>
    /// It stopped being a conversion when the searches migrated, but it stays a filter: the arms it
    /// guards all answer an object or <c>null</c> already, and this is where that invariant is
    /// stated. <c>DomBridge/JsObjects.NonElementNodes.cs</c> is the other caller.
    /// </remarks>
    internal static JsValue FromEngineResult(JsValue value) => value.IsObject ? value : JsValue.Null;
}
