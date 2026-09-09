namespace Broiler.HtmlBridge;

// The two constant-answer native function factories — UndefinedFunction and NullFunction — are gone
// from here, and so are the TrueFunction and ZeroFunction that stood beside them.
//
// WHY THEY WERE HERE, AND WHY THEY ARE NOT. A DOM member that answers a constant and reads nothing
// still needs a function object, and before the realm could mint one these four built it as the
// engine's own. They were constructable engine functions rather than the bridge's non-constructable
// DOM callable, which is
// observable — navigator.plugins.item.prototype was an object and `new navigator.plugins.item()` did
// not throw — and each module that migrated recorded at its own call site that it was deliberately
// keeping or deliberately correcting that difference: see Features/NavigatorCapabilityBinding.cs,
// Features/ScreenOrientationBinding.cs, Features/SubDocumentBinding.cs, Features/SvgElementBinding.cs
// and Features/TableBinding.cs, each of which says which it chose and why.
//
// The last of those call sites went with the last of those modules. Nothing in the repository names
// either factory now, so what is left is dead private code: there is no name for a page to reach and
// no member for Object.getOwnPropertyNames to see, and removing it changes no observable behaviour.
// The file stays as this note because five migrated modules point at it for the history.
public sealed partial class DomBridge
{
}
