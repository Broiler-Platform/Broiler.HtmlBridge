using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

public sealed partial class DomBridge
{
    // Phase 3: the DOM traversal surface (NodeFilter, TreeWalker, NodeIterator, Range and
    // createComment) is installed by the co-located TraversalBinding feature module. This thin
    // entry point keeps the historical registration call site source-compatible.
    //
    // Both sides speak JSEAL now, so the document wrapper crosses as a handle over the same object
    // and nothing else crosses at all: the module reaches this realm through ITraversalHost.Realm
    // rather than being handed a script context. The two parameters this had — a context it did not
    // pass on and an engine object it converted — were the shape of the half-migrated seam, and the
    // seam is gone.
    private void RegisterDocumentTraversalApis(JsValue document) =>
        _traversal.RegisterDocumentApis(document);
}
