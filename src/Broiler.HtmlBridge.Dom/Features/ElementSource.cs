using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;
using Broiler.JavaScript.Runtime;

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
/// <b>This is the one source now; the engine-shaped <see cref="ElementSource"/> below is what is left
/// of the pair.</b> The two used to be twins declared in two files — this one under the name
/// <c>JsElementSource</c> beside the element-interface installer in <c>DomBridge/ElementInterface.cs</c>
/// — because a member installed by <c>FastAddProperty</c> saw the engine's argument frame while one
/// minted through <see cref="IJsRealm"/> sees a <see cref="JsCall"/>, and the installer had to hand
/// each module the frame its own signature named. With the element-interface hubs migrated, the JSEAL
/// frame is the one the installer speaks and the twins are one declaration again.
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

/// <summary>
/// <see cref="JsElementSource"/> read through the engine's own argument frame, for the two feature
/// modules that still install their members with <c>FastAddValue</c>.
/// </summary>
/// <remarks>
/// <para>
/// It answers the same question by the same rule — the element-interface installer builds one of
/// these out of the JSEAL source rather than resolving the receiver a second time, so the two cannot
/// drift. It exists only because <see cref="DialogBinding.InstallElementMembers"/> and
/// <see cref="FormControlBinding.InstallHtmlElementMembers"/> declare their installers against it;
/// those two are what pin the engine vocabulary here, and this declaration goes with them.
/// </para>
/// </remarks>
internal delegate DomElement ElementSource(in Arguments a, string member);
