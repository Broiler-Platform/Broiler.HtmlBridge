using Broiler.Dom;

namespace Broiler.HtmlBridge;

/// <summary>
/// The top layer inside a nested browsing context — a modal <c>&lt;dialog&gt;</c> or an open
/// popover in a frame.
/// <para>
/// The renderer's UA sheet has no <c>dialog:modal</c> rule; the bridge supplies it, baking
/// <c>position: fixed</c>, the <c>inset: 0</c>/<c>margin: auto</c> centring, the top-layer marker
/// and the <c>::backdrop</c> scrim onto the element before serialization. That pass walked
/// <see cref="DomBridge.Elements"/> and the main document's tree, and a frame's document is severed
/// from both — so a modal dialog inside a frame got none of it. It laid out as an ordinary
/// absolutely-positioned box in its <c>&lt;body&gt;</c> and painted with no scrim behind it: in WPT
/// <c>css-view-transitions/dialog-in-rtl-iframe</c> the dialog landed in the frame's top corner
/// (right, the document being <c>dir=rtl</c>) instead of centred, over white instead of the
/// backdrop's grey.
/// </para>
/// <para>
/// Each frame is its own document, and everything the passes read is already per-document — the
/// cascade is resolved by that document's own style scope
/// (<see cref="DomBridge.GetSyncedScopedEngine"/>), and centring depends only on what the author
/// declared, not on resolved pixels. So the same passes are simply run again, once per frame.
/// </para>
/// </summary>
public sealed partial class DomBridge
{
    /// <summary>
    /// Applies the top-layer passes to every nested browsing context: the UA modal-dialog and
    /// popover positioning, then the <c>::backdrop</c> each generates.
    /// </summary>
    /// <remarks>
    /// The anchor-positioning machinery around these passes in the main document
    /// (<c>anchor()</c>, <c>position-area</c>, <c>position-try</c>) is not run here: it resolves
    /// against geometry this bridge only measures for the main frame. What is run needs no
    /// geometry. A frame's backdrop therefore takes the native marker path, and an author
    /// <c>::backdrop</c> with <c>position-try-fallbacks</c> — which needs the synthesized
    /// <c>&lt;div&gt;</c> and the viewport it is sized against — falls back to the frame's own
    /// viewport rather than the main one.
    /// </remarks>
    private void ApplySubDocumentTopLayer(
        Dictionary<string, AnchorInfo> anchorRegistry,
        Dictionary<string, Dictionary<string, string>> positionTryRules)
    {
        // Snapshot: a pass below can materialise a frame (reading a computed style loads a
        // sub-document), which would otherwise mutate the map mid-iteration.
        foreach (var contentDocument in _browsingContexts.ContentDocuments.ToList())
        {
            if (GetDocumentElement(contentDocument) is not { } subRoot)
                continue;

            var elements = subRoot.InclusiveDescendants().OfType<DomElement>().ToList();
            ApplyDialogUAPositioning(elements);
            ApplyPopoverUAPositioning(elements);

            var (frameWidth, frameHeight) = GetViewportForDocRoot(subRoot);
            InsertDialogBackdrops(subRoot, frameWidth, frameHeight, anchorRegistry, positionTryRules);
        }
    }
}
