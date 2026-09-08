namespace Broiler.HtmlBridge;

// The five propagation-control callbacks of the synthetic window event — stopPropagation,
// stopImmediatePropagation, preventDefault and the legacy cancelBubble/returnValue setters — are gone
// from here.
//
// WHY THEY WERE HERE, AND WHY THEY ARE NOT. They took an engine argument frame because their only
// caller, DispatchWindowEvent in DomBridge.WindowLoad.cs, installed each of them as an engine function
// over `ref` locals it owned: the installer minted an engine function because the body took the
// engine frame, and the body took the engine frame because the installer minted an engine function.
// That cycle only breaks when both change together, and both are in one file — so when DomBridge.WindowLoad.cs
// migrated, the five became local functions closing on the same four locals the `ref` parameters used
// to carry, in the shape Features/LegacyEventBinding.cs already had for the same five operations on a
// createEvent object. See the remarks on DispatchWindowEvent for that reasoning in full.
//
// Nothing called these afterwards: they were five private methods with a single call site, and the
// call site took its bodies with it. Removing dead private code changes no observable behaviour —
// there is no name for a page to reach and no member for Object.getOwnPropertyNames to see.
public sealed partial class DomBridge
{
}
