using System;
using System.Collections.Generic;
using Broiler.Dom;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// A window's <c>frames</c>: its document's child frames, live, by index and by name.
/// </summary>
/// <remarks>
/// <para>
/// <b>By name is what was missing.</b> HTML's named access on a window answers a child frame for its
/// browsing-context name (§7.2.2.3), so <c>parent.frames['a-xyz']</c> is the frame named
/// <c>a-xyz</c>. <c>frames</c> was a fresh array of the frames' windows, which had indices and no
/// names. reCAPTCHA's challenge frame finds the checkbox frame beside it exactly that way: it reads
/// its own <c>window.name</c>, changes the prefix and looks the result up in <c>parent.frames</c>.
/// </para>
/// <para>
/// <b>Still not the window itself.</b> In a browser <c>frames === window</c>, and the window answers
/// the indices and names. The top window here is the realm's global object, which cannot be given
/// indexed or named lookup after the fact, so the list is an object of its own: one per document, so
/// <c>frames === frames</c> holds at least, which the array did not.
/// </para>
/// <para>
/// <b>Every child frame is listed, whatever its origin.</b> A browser counts a cross-origin frame and
/// answers it as a window that can only be posted to; the array left it out. Which window a script
/// gets is decided when it asks (<see cref="SubWindowBinding.WindowAsSeen"/>).
/// </para>
/// </remarks>
internal sealed class WindowFrames(
    Func<IReadOnlyList<DomElement>> containers,
    Func<DomElement, JsValue> windowOf,
    Func<DomElement, string> nameOf) : IJsExotic
{
    /// <summary>The frames as of the last <see cref="IndexedLength"/> ask, which the indices are read out of.</summary>
    /// <remarks>See <c>DomCollection</c>'s field of the same purpose: the provider asks for the length
    /// immediately before it reads an index, so one walk of the document serves one access.</remarks>
    private IReadOnlyList<DomElement>? _containers;

    /// <inheritdoc />
    public uint IndexedLength
    {
        get
        {
            _containers = containers();
            return (uint)_containers.Count;
        }
    }

    /// <inheritdoc />
    public bool TryGetIndex(uint index, out JsValue value)
    {
        var frames = _containers ??= containers();
        if (index < (uint)frames.Count)
        {
            value = windowOf(frames[(int)index]);
            return true;
        }

        value = JsValue.Undefined;
        return false;
    }

    /// <inheritdoc />
    public bool TryGetNamed(string name, out JsValue value)
    {
        if (name == "length")
        {
            value = JsValue.Number((_containers ?? containers()).Count);
            return true;
        }

        // The first frame in tree order with that name. An empty name names nothing: a frame without
        // one is not found by the empty string.
        if (name.Length > 0)
        {
            foreach (var container in containers())
            {
                if (string.Equals(nameOf(container), name, StringComparison.Ordinal))
                {
                    value = windowOf(container);
                    return true;
                }
            }
        }

        value = JsValue.Undefined;
        return false;
    }

    /// <summary>Never: a window has no named setter, so an assignment is an ordinary one.</summary>
    public bool TrySetNamed(string name, JsValue value) => false;

    /// <summary>
    /// None: a window's named properties are not enumerated (<c>[LegacyUnenumerableNamedProperties]</c>),
    /// so <c>Object.keys(frames)</c> is the indices.
    /// </summary>
    public IReadOnlyList<string> SupportedNames => [];
}
