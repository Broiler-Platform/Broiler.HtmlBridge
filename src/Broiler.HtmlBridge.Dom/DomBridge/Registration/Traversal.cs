using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Engine;

namespace Broiler.HtmlBridge;

public sealed partial class DomBridge
{
    // Phase 3: the DOM traversal surface (NodeFilter, TreeWalker, NodeIterator, Range and
    // createComment) is installed by the co-located TraversalBinding feature module. This thin
    // entry point keeps the historical registration call site source-compatible.
    private void RegisterDocumentTraversalApis(JSContext context, JSObject document) =>
        _traversal.RegisterDocumentApis(context, document);
}
