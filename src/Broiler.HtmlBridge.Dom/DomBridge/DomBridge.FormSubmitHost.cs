using System.Collections.Generic;
using Broiler.Dom;
using Broiler.HtmlBridge.Dom.Runtime;

namespace Broiler.HtmlBridge;

// Explicit IFormSubmitHost implementation for the FormSubmitBinding feature module (Phase 3): the bridge
// exposes read access to the live per-node listener store via an explicit interface member, so the
// submit action never reaches an arbitrary bridge private field and the public surface is unchanged.
public sealed partial class DomBridge : Dom.Features.IFormSubmitHost
{
    Dictionary<string, List<EventListenerRegistration>> Dom.Features.IFormSubmitHost.GetEventListeners(DomNode node)
        => GetEventListeners(node);
}
