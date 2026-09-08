namespace Broiler.HtmlBridge;

public sealed partial class DomBridge
{
    // Phase 3: the DOM traversal surface (NodeFilter, TreeWalker, NodeIterator, Range and
    // createComment) is installed by the co-located TraversalBinding feature module. This thin
    // entry point keeps the historical registration call site source-compatible.
    //
    // The module speaks JSEAL now, so the document wrapper crosses the seam as a handle over the
    // same engine object (Dom.Runtime.JsInterop) and the context is no longer passed on: the module
    // reaches this realm through ITraversalHost.Realm. Both parameters stay because
    // DomBridge/Registration/Registration.cs, which calls this, is still engine-typed.
    private void RegisterDocumentTraversalApis(Broiler.JavaScript.Engine.JSContext context, Broiler.JavaScript.Runtime.JSObject document) =>
        _traversal.RegisterDocumentApis(Dom.Runtime.JsInterop.FromEngineObject(document));
}
