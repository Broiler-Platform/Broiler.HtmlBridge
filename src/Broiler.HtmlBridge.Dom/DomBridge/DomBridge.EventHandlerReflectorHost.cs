using System.Collections.Generic;
using Broiler.Dom;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge;

// Explicit IEventHandlerReflectorHost implementation for the EventHandlerReflectorBinding feature module
// (Phase 3): the bridge exposes read/write access to the live inline on* handler map via an explicit
// interface member, so the reflector module never reaches an arbitrary bridge private field and the
// public surface is unchanged.
public sealed partial class DomBridge : Dom.Features.IEventHandlerReflectorHost
{
    Dictionary<string, JSValue> Dom.Features.IEventHandlerReflectorHost.GetInlineEventHandlers(DomNode node)
        => GetInlineEventHandlers(node);
}
