using System.Collections.Generic;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

/// <summary>
/// <c>HTMLTemplateElement</c>'s contents fragment: who owns a template's children, when they move
/// there, and the two places that have to follow them (HTML §4.12.3).
/// </summary>
/// <remarks>
/// Split out of <c>Utilities.cs</c> and <c>DomBridge.Serialization.cs</c> rather than left in them.
/// The pieces are one concern — the fragment, the parse-time divert that fills it, and the
/// serialization walk that reaches through to it — and the two files they came from are both over
/// the architecture guard's line limit, which asks for a feature partial rather than a fatter god
/// object.
/// </remarks>
public sealed partial class DomBridge
{
    /// <summary>Per-template contents fragment, minted on first access and kept stable
    /// afterwards, so <c>t.content === t.content</c> and a mutation through it survives.</summary>
    private readonly Dictionary<DomElement, DomDocumentFragment> _templateContents = new();

    /// <summary>
    /// The fragment behind <c>HTMLTemplateElement.content</c> — the template's <b>own</b> children,
    /// held in the fragment the specification puts them in rather than copied out of the tree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// HTML §4.12.3 has the parser put a template's children straight into this fragment, leaving
    /// the element itself childless, and that is now what happens: <see cref="DivertTemplateContents"/>
    /// moves them at the end of the parse. The element is the owner of the fragment, not a second
    /// copy of it.
    /// </para>
    /// <para>
    /// It used to build the fragment from a deep <em>copy</em>, leaving the children in the tree,
    /// and the deviations that followed were not confined to the two sides disagreeing. A template's
    /// contents were reachable from the document — <c>t.querySelector('.row')</c> found them, where
    /// a browser answers <c>null</c> because they are not in the tree at all — so a page walking
    /// itself processed markup it was meant to stamp later. And writing <c>t.innerHTML</c> rewrote
    /// the element's children while <c>content</c> kept the cached copy, so building a template
    /// dynamically and then stamping it produced the <em>old</em> markup, silently.
    /// </para>
    /// <para>
    /// A template created by <c>createElement</c> starts empty and stays that way: only the parser
    /// diverts, so <c>t.appendChild(x)</c> appends to the element as it does in a browser, and only
    /// <c>t.innerHTML</c> and <c>t.content</c> reach the fragment.
    /// </para>
    /// </remarks>
    private DomDocumentFragment GetTemplateContent(DomElement template)
    {
        if (_templateContents.TryGetValue(template, out var existing))
            return existing;

        var fragment = CreateBridgeDocumentFragment();
        _templateContents[template] = fragment;
        return fragment;
    }

    /// <summary>
    /// Moves every parsed <c>&lt;template&gt;</c>'s children into its contents fragment, so the
    /// element is left childless as HTML §4.12.3 requires. Runs once, at the end of the parse.
    /// </summary>
    /// <remarks>
    /// Depth-first over the whole tree including inside templates, because a template may contain
    /// another: the inner one is diverted into the outer one's fragment first and must still be
    /// diverted itself. Declarative shadow-root templates are already gone by this point — that pass
    /// consumes them — so what is left here is the inert kind.
    /// </remarks>
    private void DivertTemplateContents(DomNode root)
    {
        foreach (var node in root.InclusiveDescendants().ToArray())
        {
            if (node is not DomElement element ||
                !string.Equals(element.TagName, "template", StringComparison.OrdinalIgnoreCase))
                continue;

            var fragment = GetTemplateContent(element);
            foreach (var child in element.ChildNodes.ToArray())
            {
                element.RemoveChild(child);
                fragment.AppendChild(child);
                DivertTemplateContents(child);
            }
        }
    }

    /// <summary>The node list serialization walks for <paramref name="node"/>: a template's contents
    /// fragment stands in for its (empty) own child list.</summary>
    private IEnumerable<DomNode> SerializationChildrenOf(DomNode node) =>
        node is DomElement element && IsTemplateElement(element)
            ? GetTemplateContent(element).ChildNodes
            : node.ChildNodes;

    /// <summary>Whether <paramref name="element"/> is an HTML <c>&lt;template&gt;</c>.</summary>
    internal static bool IsTemplateElement(DomElement element) =>
        string.Equals(element.TagName, "template", StringComparison.OrdinalIgnoreCase);
}
