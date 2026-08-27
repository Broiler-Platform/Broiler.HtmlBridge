using Broiler.Dom;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.HtmlBridge.Logging;

namespace Broiler.HtmlBridge;

/// <summary>
/// <c>startViewTransition()</c> on a nested browsing context's document — the cross-document half of
/// CSS View Transitions, where a page drives a transition inside its <c>&lt;iframe&gt;</c> through
/// <c>frame.contentDocument</c>.
/// <para>
/// The method was simply absent from the sub-document surface, so
/// <c>iframeDocument.startViewTransition(…)</c> threw a <c>TypeError</c> — and that aborted the rest
/// of the driving script, taking the <em>main</em> frame's transition down with it. The WPT
/// <c>css-view-transitions/iframe-and-main-frame-transition-*</c> family is exactly that shape, which
/// is why its "old main" members scored 0%: the main document never got as far as its own
/// <c>startViewTransition</c> (issue #1552 problems 5 and 6).
/// </para>
/// <para>
/// A sub-document is not laid out by this bridge — it is serialized back into its container's
/// <c>srcdoc</c> and rasterised on its own by the image renderer — so there is no geometry here to
/// snapshot per named element, and the main document's capture machinery cannot be pointed at it.
/// What the reftests in this family actually pin is narrower: they pause the animations and set
/// <c>::view-transition-old(root) { opacity: 1 }</c> against
/// <c>::view-transition-new(root) { opacity: 0 }</c>, so the frame must show the document as it stood
/// <em>before</em> the update callback. That is reproduced exactly by holding the pre-callback
/// serialization and emitting it in place of the live sub-tree — the whole "old root snapshot" for a
/// document whose only capture is the implicit root.
/// </para>
/// <para>
/// When the rules leave the new state showing (the default, and what every other member of the family
/// pins), nothing is held and the live sub-document serializes as usual.
/// </para>
/// </summary>
public sealed partial class DomBridge
{
    /// <summary>The pre-callback serialization of each sub-document with a transition running,
    /// keyed by its browsing-context root.</summary>
    private readonly Dictionary<DomNode, string> _subDocumentViewTransitionOldMarkup = [];

    /// <summary>
    /// <c>subDocument.startViewTransition(updateCallback)</c> /
    /// <c>startViewTransition({ update, types })</c>. Holds the sub-document's current markup as the
    /// "old" state, runs the update callback synchronously (its mutation is the "new" state), and
    /// returns a <c>ViewTransition</c> whose promises are already resolved — the reftests gate their
    /// screenshot on <c>ready</c>.
    /// </summary>
    internal JSValue StartSubDocumentViewTransition(DomNode docRoot, in Arguments arguments)
    {
        JSFunction? updateCallback = null;
        if (arguments.Length > 0)
        {
            if (arguments[0] is JSFunction fn)
                updateCallback = fn;
            else if (arguments[0] is JSObject options && options[(KeyString)"update"] is JSFunction updateFn)
                updateCallback = updateFn;
        }

        // Captured before the callback runs, so it is genuinely the old state. A sub-document that
        // cannot be serialized (no content root yet) simply holds nothing and renders live.
        if (SerializeSubDocumentChildren(docRoot) is { Length: > 0 } oldMarkup)
            _subDocumentViewTransitionOldMarkup[docRoot] = oldMarkup;

        if (updateCallback is not null)
        {
            try
            {
                updateCallback.InvokeFunction(new Arguments(updateCallback));
            }
            catch (System.Exception ex)
            {
                RenderLogger.LogWarning(LogCategory.JavaScript, "DomBridge.subDocument.startViewTransition",
                    $"View transition update callback threw: {ex.Message}", ex);
            }
        }

        var transition = new JSObject();
        transition.FastAddValue("ready", ResolvedThenable(), JSPropertyAttributes.EnumerableConfigurableValue);
        transition.FastAddValue("finished", ResolvedThenable(), JSPropertyAttributes.EnumerableConfigurableValue);
        transition.FastAddValue("updateCallbackDone", ResolvedThenable(), JSPropertyAttributes.EnumerableConfigurableValue);
        transition.FastAddValue("types", new JavaScript.BuiltIns.Array.JSArray(), JSPropertyAttributes.EnumerableConfigurableValue);
        transition.FastAddValue("skipTransition",
            new DomFunction((in _) =>
            {
                _subDocumentViewTransitionOldMarkup.Remove(docRoot);
                return JSUndefined.Value;
            }, "skipTransition", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);
        return transition;
    }

    /// <summary>The sub-document's children serialized as the markup its container's <c>srcdoc</c>
    /// carries — the same form <see cref="TrySerializeCurrentSrcDoc"/> emits.</summary>
    private string? SerializeSubDocumentChildren(DomNode docRoot)
    {
        if (docRoot.ChildNodes.Count == 0)
            return null;

        return string.Concat(ChildElements(docRoot).Select(SerializeElementToHtml));
    }

    /// <summary>
    /// What a nested browsing context is <em>displaying</em>: the old state it is holding for its own
    /// transition, else its live sub-tree. This is what the container's <c>srcdoc</c> carries and what
    /// an enclosing document's root snapshot records, so the two can never disagree about what was on
    /// screen.
    /// </summary>
    private string? EffectiveSubDocumentMarkup(DomNode docRoot) =>
        TryGetHeldSubDocumentViewTransitionMarkup(docRoot) ?? SerializeSubDocumentChildren(docRoot);

    /// <summary>
    /// The markup a frame renders right now, resolving the two things that can outrank its live
    /// sub-tree — an enclosing document frozen on a root snapshot taken before the frame changed,
    /// then the frame's own transition holding its old root. Shared by both routes a frame's content
    /// reaches the renderer by: the <c>srcdoc</c> attribute it round-trips through, and the stamped
    /// live document a <c>src</c> frame carries (see <c>DomBridge.FrameDocumentProjection.cs</c>).
    /// </summary>
    private string? RenderedSubDocumentMarkup(DomNode docRoot) =>
        TryGetFrameMarkupHeldByRootSnapshot(docRoot) ?? EffectiveSubDocumentMarkup(docRoot);

    /// <summary>
    /// The markup a frame must show because an <em>enclosing</em> document is frozen displaying a root
    /// snapshot captured while the frame looked like that — or <c>null</c> when no such snapshot is on
    /// screen.
    /// <para>This outranks the frame's own transition: the page is showing a picture taken at a moment
    /// in the past, and everything in that picture, frames included, has to be from that moment.</para>
    /// </summary>
    private string? TryGetFrameMarkupHeldByRootSnapshot(DomNode docRoot)
    {
        if (_activeViewTransition is not { } transition
            || !transition.FrameMarkupAtCapture.TryGetValue(docRoot, out var markup))
        {
            return null;
        }

        return DocumentElement is { } root
            && RootSnapshotShowsOldState(CollectViewTransitionPseudoDeclarations(root))
            ? markup
            : null;
    }

    /// <summary>
    /// The held pre-callback markup for <paramref name="docRoot"/> when its own
    /// <c>::view-transition-*</c> rules leave the <em>old</em> root snapshot showing, otherwise
    /// <c>null</c> so the live sub-document serializes.
    /// </summary>
    private string? TryGetHeldSubDocumentViewTransitionMarkup(DomNode docRoot)
    {
        if (!_subDocumentViewTransitionOldMarkup.TryGetValue(docRoot, out var oldMarkup))
            return null;

        if (GetDocumentElement(docRoot) is not { } subRoot)
            return null;

        var pseudoRules = CollectViewTransitionPseudoDeclarations(subRoot);
        return RootSnapshotShowsOldState(pseudoRules) ? oldMarkup : null;
    }

    /// <summary>
    /// Whether the pseudo rules pin the root's <em>old</em> snapshot over its new one — the
    /// <c>::view-transition-old(root) { opacity: 1 }</c> / <c>-new(root) { opacity: 0 }</c> pairing
    /// the reftests use to freeze a paused transition at its start. Anything else (including the
    /// unpinned default, whose animation would have run to the new state) shows the new state.
    /// </summary>
    private static bool RootSnapshotShowsOldState(Dictionary<string, Dictionary<string, string>> pseudoRules)
    {
        return IsOpaqueOpacity(OpacityFor("old")) && IsTransparentOpacity(OpacityFor("new"));

        // A name-specific rule wins over the universal one, matching LookupPseudo's cascade order.
        string? OpacityFor(string kind)
        {
            if (pseudoRules.TryGetValue($"{kind}|root", out var byName)
                && byName.TryGetValue("opacity", out var specific))
                return specific;
            return pseudoRules.TryGetValue($"{kind}|*", out var universal)
                && universal.TryGetValue("opacity", out var shared) ? shared : null;
        }
    }

    private static bool IsOpaqueOpacity(string? value) =>
        TryParseOpacity(value, out double opacity) && opacity >= 0.5;

    private static bool IsTransparentOpacity(string? value) =>
        TryParseOpacity(value, out double opacity) && opacity < 0.5;

    private static bool TryParseOpacity(string? value, out double opacity) =>
        double.TryParse(value?.Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out opacity);
}
