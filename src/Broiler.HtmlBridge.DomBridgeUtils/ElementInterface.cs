using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    /// <summary>
    /// Argument zero as a string — the ECMAScript coercion, which may run a <c>toString</c> the page
    /// wrote — or the empty string when nothing was passed.
    /// </summary>
    /// <remarks>
    /// The selector and collection members read their argument here rather than inside
    /// <c>Dom.Features.SelectorsBinding</c>, because the module's entry points take the string
    /// their caller has already produced and this file is their only caller; the sub-document and
    /// <c>DocumentFragment</c> forms read their own (this said they shared them). It is the realm's
    /// <c>ToString</c> and not the handle's rendering, the same read the engine frame performed.
    /// </remarks>
    internal static string StringArgument(in JsCall call) =>
        call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty;

    /// <summary>
    /// <c>tagName</c>'s value: upper-cased for an HTML element, verbatim otherwise, which is the rule
    /// the wrapper applied once when it minted the string.
    /// </summary>
    internal static string TagNameForScript(DomElement element) =>
        string.IsNullOrEmpty(element.NamespaceUri) ||
        string.Equals(element.NamespaceUri, "http://www.w3.org/1999/xhtml", StringComparison.OrdinalIgnoreCase)
            ? element.TagName.ToUpperInvariant()
            : element.TagName;
}
