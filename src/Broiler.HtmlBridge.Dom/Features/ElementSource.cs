using Broiler.Dom;
using Broiler.JSeal;

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
/// There is one source. <c>animate()</c> and <c>click</c>/<c>focus</c>/<c>blur</c> included, every
/// member the two hubs in <c>DomBridge/ElementInterface.cs</c> install asks this source with the
/// <see cref="JsCall"/> it was invoked with, so those members share exactly one rule for "which
/// element is this".
/// </para>
/// <para>
/// The name is <c>JsElementSource</c> rather than <c>ElementSource</c> only because six feature
/// modules outside this file declare installers against it — <c>DialogBinding</c>,
/// <c>ElementContentBinding</c>, <c>ElementGeometryBinding</c>, <c>FormControlBinding</c>,
/// <c>GlobalAttributeBinding</c> and <c>InsertAdjacentBinding</c>. Renaming it is a rename of their nine
/// signatures, the eight in the two hubs, and every doc cref, which no build checks.
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
