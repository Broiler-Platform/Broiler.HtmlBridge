using Broiler.JSeal;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The DOM <c>ChildNode</c> mixin — <c>remove()</c>, <c>before()</c>, <c>after()</c> and
/// <c>replaceWith()</c> — registered on every node wrapper, co-located as an HtmlBridge feature module
/// (Phase 3). Pure DOM tree mutation: it positions the argument nodes relative to the context node
/// through the bridge's neutral static tree helpers (<c>ParentEl</c>, <c>ChildIndexOf</c>,
/// <c>RemoveNthChild</c>, <c>SetParent</c>) and drives the side-effecting insertion plus the style-scope
/// invalidation through the <see cref="IChildNodeHost"/> contract.
/// Was the bridge's <c>JsJsObjectsRemove093Core</c>..<c>ReplaceWith096Core</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>One entry point per operation again, now that all three installers share a call frame.</b> The
/// mixin is installed on <c>Node</c>'s side of the tree by <c>DomBridge/NodeInterfaces.cs</c>
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
        var parentElement = element.ParentElement;
        element.Remove();
        if (parentElement is not null)
            host.InvalidateStyleScope(parentElement);
        return JsValue.Undefined;
    }

    public static JsValue Before(IChildNodeHost host, DomNode element, in JsCall call)
    {
        var parentElement = element.ParentElement;
        if (element.ParentNode == null || call.Length == 0)
            return JsValue.Undefined;
        element.Before(host.BuildChildNodeArgumentNodes(call.Arguments));
        if (parentElement is not null)
            host.InvalidateStyleScope(parentElement);
        return JsValue.Undefined;
    }

    public static JsValue After(IChildNodeHost host, DomNode element, in JsCall call)
    {
        var parentElement = element.ParentElement;
        if (element.ParentNode == null || call.Length == 0)
            return JsValue.Undefined;
        element.After(host.BuildChildNodeArgumentNodes(call.Arguments));
        if (parentElement is not null)
            host.InvalidateStyleScope(parentElement);
        return JsValue.Undefined;
    }

    public static JsValue ReplaceWith(IChildNodeHost host, DomNode element, in JsCall call)
    {
        var parentElement = element.ParentElement;
        if (element.ParentNode == null)
            return JsValue.Undefined;
        element.ReplaceWith(host.BuildChildNodeArgumentNodes(call.Arguments));
        if (parentElement is not null)
            host.InvalidateStyleScope(parentElement);
        return JsValue.Undefined;
    }
}
