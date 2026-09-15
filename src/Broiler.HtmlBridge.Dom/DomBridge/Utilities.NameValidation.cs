// NO ENGINE NAMESPACE. This file used to open with four of them, all for ThrowDOMException and the
// validators that forward to it, and a note saying the parameter type could not change until the
// five files handing it a context asked with a realm instead. They now do, so the four usings are
// gone and this file is the realm's throughout.
//
// What made it a single commit rather than five is that the parameter is load-bearing in one
// direction only: nothing here reads the context except to reach the page's DOMException
// constructor, and IJsCalls.DomError reaches the same global on the same realm with the same
// fallback. See the remarks on ThrowDOMException for why that is a rename and not a behaviour
// change.
using Broiler.Dom;

namespace Broiler.HtmlBridge;

/// <summary>
/// Sibling partial peeled out of <c>Utilities.cs</c> (Phase 3 ratchet, 2026-07-17) to keep it
/// under the 750-line guard: the cohesive DOM element/qualified-name validation cluster together
/// with the JS-side constructor globals it validates against — the <c>DOMException</c> constructor
/// (and the C# helper that throws it), plus the <c>Node</c> and <c>SVGLength</c> constant carriers.
/// The spec name-validation algorithm itself now lives in the canonical
/// <see cref="DomNameValidation"/> (Broiler.Dom); the bridge only marshals the thrown
/// <see cref="DomException"/> into a JavaScript <c>DOMException</c>.
/// </summary>
public sealed partial class DomBridge
{
    // ------------------------------------------------------------------
    //  Element name validation
    // ------------------------------------------------------------------

    /// <summary>
    /// Validates a selector argument (DOM §4.2.6), throwing <c>SyntaxError</c> when it does not parse
    /// as a selector list.
    /// </summary>
    /// <remarks>
    /// Shared by all five scripted entry points that take one — <c>querySelector</c>,
    /// <c>querySelectorAll</c>, <c>matches</c> and <c>closest</c> on an element, the two document
    /// forms, the sub-document forms, and the <c>DocumentFragment</c> forms — because a browser throws
    /// from all of them identically, which was measured rather than assumed. The CSS cascade does not
    /// come through here and stays lenient, as CSS error handling requires.
    /// <para>
    /// <b>This one takes no parameter at all, where its three neighbours above take a realm.</b> It
    /// went first, when every one of its callers was already in the migrating group and the three
    /// above still had callers holding a context — and the argument it made then is the one that
    /// moved them since: the nullable parameter was only ever forwarded to
    /// <see cref="DomBridgeUtils.ThrowDOMException"/>, and <c>IJsCalls.DomError</c> constructs through the same
    /// <c>DOMException</c> global against the same realm, so what a page catches is unchanged. The
    /// null-tolerance survives as the realm's: before <c>Attach</c> there is no realm and the check
    /// is skipped, which is what a <see langword="null"/> context meant and is the state the
    /// bridge's own pre-attach selector work runs in.
    /// </para>
    /// </remarks>
    internal void ValidateSelector(string selector)
    {
        if (_realm is { } realm && !Dom.Features.DomApiSyntax.IsValidSelectorList(selector))
        {
            throw realm.DomError(
                "SyntaxError",
                $"Failed to execute 'querySelector' on 'Document': '{selector}' is not a valid selector.");
        }
    }
}
