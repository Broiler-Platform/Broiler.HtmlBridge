using Broiler.HtmlBridge.Jseal;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The DOM <c>ChildNode</c> mixin — <c>remove()</c>, <c>before()</c>, <c>after()</c> and
/// <c>replaceWith()</c> — registered on every node wrapper, co-located as an HtmlBridge feature module
/// (Phase 3). Pure DOM tree mutation: it positions the argument nodes relative to the context node
/// through the bridge's neutral static tree helpers (<c>ParentEl</c>, <c>ChildIndexOf</c>,
/// <c>RemoveNthChild</c>, <c>SetParent</c>) and drives the side-effecting insertion / removal plus the
/// node-iterator / mutation-observer notifications through the <see cref="IChildNodeHost"/> contract.
/// Was the bridge's <c>JsJsObjectsRemove093Core</c>..<c>ReplaceWith096Core</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>One entry point per operation again, now that all three installers share a call frame.</b> The
/// mixin is installed on <c>Node</c>'s side of the tree by <c>DomBridge/CharacterDataInterface.cs</c>
/// and <c>DomBridge/JsObjects.NonElementNodes.cs</c>, and on <c>Element.prototype</c> by
/// <c>DomBridge/ElementInterface.cs</c>; all three mint through the realm, so every one of them
/// arrives on a <see cref="JsCall"/>. The <c>…Core</c> helpers below stay the only place the DOM steps
/// are written.
/// </para>
/// <para>
/// The file briefly carried a second, engine-framed entry point per operation, because
/// <c>ElementInterface.cs</c> installed these four with the engine's own argument frame and there is
/// no adapter between two call frames — only between two object types. That installer has migrated, so
/// the duplicate entry points, the engine-framed argument builder on <see cref="IChildNodeHost"/>
/// and this file's engine <c>using</c>s are gone with it.
/// </para>
/// </remarks>
internal static class ChildNodeBinding
{
    public static JsValue Remove(IChildNodeHost host, DomNode element, in JsCall _)
    {
        RemoveCore(host, element);
        return JsValue.Undefined;
    }

    public static JsValue Before(IChildNodeHost host, DomNode element, in JsCall call)
    {
        var parent = DomBridge.ParentEl(element);
        if (parent == null || call.Length == 0)
            return JsValue.Undefined;
        BeforeCore(host, parent, element, host.BuildChildNodeArgumentNodes(call.Arguments));
        return JsValue.Undefined;
    }

    public static JsValue After(IChildNodeHost host, DomNode element, in JsCall call)
    {
        var parent = DomBridge.ParentEl(element);
        if (parent == null || call.Length == 0)
            return JsValue.Undefined;
        AfterCore(host, parent, element, host.BuildChildNodeArgumentNodes(call.Arguments));
        return JsValue.Undefined;
    }

    public static JsValue ReplaceWith(IChildNodeHost host, DomNode element, in JsCall call)
    {
        var parent = DomBridge.ParentEl(element);
        if (parent == null)
            return JsValue.Undefined;
        var replacementIndex = DomBridge.ChildIndexOf(parent, element);
        if (replacementIndex < 0)
            return JsValue.Undefined;
        ReplaceWithCore(host, parent, element, replacementIndex, host.BuildChildNodeArgumentNodes(call.Arguments));
        return JsValue.Undefined;
    }

    // -------- The DOM steps, written once --------

    /// <remarks>
    /// Capture the parent up front: it is computed from the canonical ParentNode, and the removal
    /// detaches it — reading it after would return null (→ NRE in InvalidateStyleScope). Mirrors the
    /// removeChild path, which holds the parent independently.
    /// <para>
    /// The parent is a DomNode, not a DomElement: the document element's parent is the DomDocument, so
    /// narrowing to an element here made <c>document.documentElement.remove()</c> a silent no-op and the
    /// removed page kept rendering (WPT html/rendering
    /// Document-documentElement-remove-clears-content). The tree helpers below all take a DomNode
    /// parent already; only style-scope invalidation needs an element anchor, and a document parent has
    /// no style scope to invalidate.
    /// </para>
    /// </remarks>
    private static void RemoveCore(IChildNodeHost host, DomNode element)
    {
        var parent = element.ParentNode;
        if (parent == null)
            return;

        var idx = DomBridge.ChildIndexOf(parent, element);
        if (idx < 0)
            return;

        host.NotifyNodeIteratorPreRemoval(element);
        DomBridge.RemoveNthChild(parent, idx);
        DomBridge.SetParent(element, null);
        if (parent is DomElement parentElement)
            host.InvalidateStyleScope(parentElement);
        host.NotifyChildRemoved(parent, element, idx);
    }

    private static void BeforeCore(IChildNodeHost host, DomElement parent, DomNode element, List<DomNode> nodes)
    {
        var insertIndex = DomBridge.ChildIndexOf(parent, element);
        if (insertIndex < 0)
            return;
        foreach (var node in nodes)
            host.InsertNodeAt(parent, node, insertIndex++);
    }

    private static void AfterCore(IChildNodeHost host, DomElement parent, DomNode element, List<DomNode> nodes)
    {
        var insertIndex = DomBridge.ChildIndexOf(parent, element);
        if (insertIndex < 0)
            return;
        insertIndex++;
        foreach (var node in nodes)
            host.InsertNodeAt(parent, node, insertIndex++);
    }

    private static void ReplaceWithCore(
        IChildNodeHost host, DomElement parent, DomNode element, int replacementIndex, List<DomNode> nodes)
    {
        host.NotifyNodeIteratorPreRemoval(element);
        DomBridge.RemoveNthChild(parent, replacementIndex);
        DomBridge.SetParent(element, null);
        host.InvalidateStyleScope(parent);
        host.NotifyChildRemoved(parent, element, replacementIndex);
        foreach (var node in nodes)
            host.InsertNodeAt(parent, node, replacementIndex++);
    }
}
