using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// Where a DOM member finds the element it operates on: the one captured when the wrapper was built,
/// or the one the call's receiver names.
/// </summary>
/// <remarks>
/// <para>
/// A member installed as an own property of a wrapper closes over its element, and a member installed
/// on an interface prototype cannot — it serves every element, so it has to resolve the receiver on
/// each call. Those are the only two answers, and this delegate is the difference between them: an
/// installer written against it produces the same member either way, so the prototype's version and
/// the pre-realm instance fallback cannot drift apart.
/// </para>
/// <para>
/// <paramref name="member"/> is passed for the error a receiver-resolving source raises when the
/// receiver is not an element — <c>Element.prototype.getAttribute.call({}, 'x')</c> is a
/// <c>TypeError</c> naming the member, as it is in a browser. A capturing source ignores it.
/// </para>
/// <para>
/// <b>There is one source now.</b> It used to be a pair: an engine-shaped twin stood beside this one
/// because a member installed by <c>FastAddProperty</c> saw the engine's argument frame while one
/// minted through <see cref="IJsRealm"/> sees a <see cref="JsCall"/>, and the element-interface
/// installer had to hand each feature module the frame its own signature named. Both hubs
/// (<c>DomBridge/ElementInterface.cs</c>, <c>DomBridge/HtmlElementInterface.cs</c>) and every module
/// they installed against now speak JSEAL, so the twin is gone and this is the only declaration.
/// The two members whose <em>bodies</em> still read the engine's frame — <c>animate()</c> and
/// <c>click</c>/<c>focus</c>/<c>blur</c>, whose modules are not migrated — ask this same source for
/// their element through a receiver-only <see cref="JsCall"/> built at their install site, so there
/// is still exactly one rule for "which element is this".
/// </para>
/// <para>
/// The name is <c>JsElementSource</c> rather than <c>ElementSource</c> only because four feature
/// modules outside this file declare their installers against it — <c>ElementContentBinding</c>,
/// <c>ElementGeometryBinding</c>, <c>GlobalAttributeBinding</c> and <c>InsertAdjacentBinding</c>.
/// Renaming it is a rename of those seven signatures and nothing else.
/// </para>
/// </remarks>
internal delegate DomElement JsElementSource(in JsCall call, string member);

/// <summary>
/// The JS wrapper a DOM member is operating on — the counterpart of <see cref="JsElementSource"/> for
/// the handful of members that need the object rather than the node.
/// </summary>
/// <remarks>
/// <c>attributes</c> and the <c>Attr</c> operations hand the owning wrapper to the map they build, so
/// an attribute node can name the element it came from. A capturing source answers the wrapper the
/// member was installed on; a receiver-resolving one answers the receiver itself.
/// </remarks>
internal delegate JsValue WrapperSource(in JsCall call, string member);
