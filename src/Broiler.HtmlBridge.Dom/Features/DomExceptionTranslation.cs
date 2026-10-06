using Broiler.Dom;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// What Broiler.DOM throws from a tree mutation, as the <c>DOMException</c> a page catches, in Chromium's
/// words: "Failed to execute 'remove' on 'Element': " and the reason.
/// </summary>
/// <remarks>
/// <para>
/// The bindings check what the specification has them check before they mutate, and throw their own
/// <c>DOMException</c>s for it. What they could not check is what happens during the mutation: a removal
/// runs the page's <c>blur</c> handler first (<see cref="DomDocument.Removing"/>), and a handler that moved
/// the node makes Broiler.DOM refuse the removal. Uncaught, that was a host exception the page could not
/// catch, ending its script.
/// </para>
/// </remarks>
internal static class DomExceptionTranslation
{
    /// <summary>Runs <paramref name="operation"/>, rethrowing a <see cref="DomException"/> as the page's.</summary>
    public static void Run(IJsRealm realm, string method, string interfaceName, Action operation)
    {
        try
        {
            operation();
        }
        catch (DomException ex)
        {
            throw realm.DomError(ex.Name, $"Failed to execute '{method}' on '{interfaceName}': {ex.Message}");
        }
    }

    /// <summary>The interface a <c>ChildNode</c> method is executed on, as Chromium names it.</summary>
    public static string ChildNodeInterface(DomNode node) => node switch
    {
        DomElement => "Element",
        DomCharacterData => "CharacterData",
        DomDocumentType => "DocumentType",
        _ => "Node",
    };
}
