using Broiler.Dom;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

/// <summary>
/// User activation (HTML §6.4.3): when the user last pressed something in each document of the page,
/// which is what lets a frame of another origin navigate the whole tab.
/// </summary>
/// <remarks>
/// <para>
/// A press of a pointer button, or of a key other than a modifier or Escape, activates the document
/// it reached and every document around it, the frames that hold it up to the page. The activation is
/// transient: it lasts <see cref="TransientActivationMilliseconds"/>, Chromium's five seconds.
/// </para>
/// <para>
/// Only navigation of the top window asks (<c>SubWindowBinding.NavigateTop</c>). Nothing consumes an
/// activation, as navigation does not.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    /// <summary>How long a document stays activated after the user's press: Chromium's activation lifespan.</summary>
    private const long TransientActivationMilliseconds = 5000;

    private readonly Dictionary<DomDocument, long> _lastActivations = new(ReferenceEqualityComparer.Instance);

    /// <summary>Records that the user has just activated <paramref name="document"/>, and so every document around it.</summary>
    private void NotifyUserActivation(DomDocument document)
    {
        var now = Environment.TickCount64;
        for (DomDocument? current = document; current is not null;)
        {
            _lastActivations[current] = now;
            var container = GetFrameForContentDocument(current);
            var outer = container is null ? null : GetOwningDocument(container);
            current = ReferenceEquals(outer, current) ? null : outer;
        }
    }

    /// <summary>Whether the user activated <paramref name="document"/> within the last <see cref="TransientActivationMilliseconds"/>.</summary>
    private bool HasTransientActivation(DomDocument document) =>
        _lastActivations.TryGetValue(document, out var at) &&
        Environment.TickCount64 - at <= TransientActivationMilliseconds;
}
