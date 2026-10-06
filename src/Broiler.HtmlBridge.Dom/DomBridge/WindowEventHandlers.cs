using System.Collections.Generic;
using Broiler.JSeal;

namespace Broiler.HtmlBridge;

/// <summary>
/// The window's event-handler attributes -- <c>onload</c>, <c>onmessage</c> and the rest -- on the
/// global object, which answers for the window whose script is running.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every document shares one global object</b>, the page's window. A frame's script that set
/// <c>onload = …</c> or <c>onmessage = …</c> without <c>window.</c> set a property of that global, so
/// it set the page's handler: its own window's <c>load</c> ran nothing, and the page's ran the frame's
/// function. Each handler is an accessor now, as a browser's are. While a frame's script runs it reads
/// and writes the frame window's handler, which is the property the frame's window dispatch reads;
/// otherwise the page's, kept here.
/// </para>
/// <para>
/// <b>The page's handlers are read from here, never through the global.</b> A dispatch at the page's
/// window can run while a frame's script is on the stack, and through the global it would find the
/// frame's handler. So the page's window dispatch reads <see cref="PageWindowHandler"/>, and a frame
/// reaches the page's through its <c>parent</c>, whose view answers these names from here too.
/// </para>
/// <para>
/// The names are HTML's GlobalEventHandlers and WindowEventHandlers. A handler holds an object, and
/// anything else sets it to <c>null</c> ([LegacyTreatNonObjectAsNull]).
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    /// <summary>The window event-handler attributes the global object answers for the running window.</summary>
    internal static readonly string[] WindowEventHandlerNames =
    [
        // WindowEventHandlers
        "onafterprint", "onbeforeprint", "onbeforeunload", "onhashchange", "onlanguagechange", "onmessage",
        "onmessageerror", "onoffline", "ononline", "onpagehide", "onpagereveal", "onpageshow", "onpageswap",
        "onpopstate", "onrejectionhandled", "onstorage", "onunhandledrejection", "onunload",

        // GlobalEventHandlers
        "onabort", "onanimationcancel", "onanimationend", "onanimationiteration", "onanimationstart",
        "onauxclick", "onbeforeinput", "onbeforematch", "onbeforetoggle", "onblur", "oncancel", "oncanplay",
        "oncanplaythrough", "onchange", "onclick", "onclose", "oncontextlost", "oncontextmenu",
        "oncontextrestored", "oncopy", "oncuechange", "oncut", "ondblclick", "ondrag", "ondragend",
        "ondragenter", "ondragleave", "ondragover", "ondragstart", "ondrop", "ondurationchange", "onemptied",
        "onended", "onerror", "onfocus", "onformdata", "ongotpointercapture", "oninput", "oninvalid",
        "onkeydown", "onkeypress", "onkeyup", "onload", "onloadeddata", "onloadedmetadata", "onloadstart",
        "onlostpointercapture", "onmousedown", "onmouseenter", "onmouseleave", "onmousemove", "onmouseout",
        "onmouseover", "onmouseup", "onpaste", "onpause", "onplay", "onplaying", "onpointercancel",
        "onpointerdown", "onpointerenter", "onpointerleave", "onpointermove", "onpointerout", "onpointerover",
        "onpointerup", "onprogress", "onratechange", "onreset", "onresize", "onscroll", "onscrollend",
        "onsecuritypolicyviolation", "onseeked", "onseeking", "onselect", "onselectionchange",
        "onselectstart", "onslotchange", "onstalled", "onsubmit", "onsuspend", "ontimeupdate", "ontoggle",
        "ontransitioncancel", "ontransitionend", "ontransitionrun", "ontransitionstart", "onvolumechange",
        "onwaiting", "onwheel",
    ];

    /// <summary>The page's own window handlers, by attribute name; one that is not here is <c>null</c>.</summary>
    private readonly Dictionary<string, JsValue> _pageWindowHandlers = new(StringComparer.Ordinal);

    /// <summary>Defines each window event-handler attribute on the global object as an accessor for the running window's.</summary>
    private void RegisterWindowEventHandlers(JsValue global)
    {
        var realm = Realm;
        foreach (var name in WindowEventHandlerNames)
        {
            var attribute = name;
            realm.DefineAccessor(global, attribute,
                (in _) => WindowEventHandlerAsSeen(attribute),
                (in call) =>
                {
                    SetWindowEventHandlerAsSeen(attribute, call.Length > 0 ? call[0] : JsValue.Undefined);
                    return JsValue.Undefined;
                });
        }
    }

    /// <summary>The running window's handler: the frame's while a frame's script runs, the page's otherwise.</summary>
    private JsValue WindowEventHandlerAsSeen(string attribute)
    {
        if (_windowContext.ResolveCurrentSubWindow() is not { } frame)
            return PageWindowHandler(attribute);

        var handler = Realm.GetProperty(frame, attribute);
        return handler.IsObject ? handler : JsValue.Null;
    }

    private void SetWindowEventHandlerAsSeen(string attribute, JsValue value)
    {
        if (_windowContext.ResolveCurrentSubWindow() is { } frame)
            Realm.SetProperty(frame, attribute, value.IsObject ? value : JsValue.Null);
        else
            SetPageWindowHandler(attribute, value);
    }

    /// <summary>The page's window handler named <paramref name="attribute"/> (<c>onload</c>), or <c>null</c>.</summary>
    internal JsValue PageWindowHandler(string attribute) =>
        _pageWindowHandlers.TryGetValue(attribute, out var handler) ? handler : JsValue.Null;

    /// <summary>Sets the page's window handler named <paramref name="attribute"/>; anything but an object clears it.</summary>
    internal void SetPageWindowHandler(string attribute, JsValue value)
    {
        if (value.IsObject)
            _pageWindowHandlers[attribute] = value;
        else
            _pageWindowHandlers.Remove(attribute);
    }

    /// <summary>Whether <paramref name="attribute"/> is one of the window's event-handler attributes.</summary>
    internal static bool IsWindowEventHandlerName(string attribute) =>
        Array.IndexOf(WindowEventHandlerNames, attribute) >= 0;

    /// <summary>
    /// The frame window a call to one of the global object's own methods stands for: the window whose
    /// script is running, when that is a frame's and the call reached the global itself -- a bare
    /// <c>addEventListener(…)</c>, whose receiver is the global in sloppy code and none in strict code.
    /// </summary>
    /// <remarks>
    /// A call through a frame's <c>parent</c> or <c>top</c> has that view as its receiver, and is the
    /// page's; a call through <c>window</c> or <c>self</c> in a frame's script reaches the frame window's
    /// own method and never comes here.
    /// </remarks>
    private bool TryGetFrameWindowForGlobalCall(in JsCall call, out JsValue frameWindow)
    {
        frameWindow = JsValue.Missing;
        if ((call.This != WindowHandle && !call.This.IsNullish) ||
            _windowContext.ResolveCurrentSubWindow() is not { } frame)
        {
            return false;
        }

        frameWindow = frame;
        return true;
    }

    /// <summary>Calls the frame window's own method <paramref name="name"/> with the call's arguments.</summary>
    private JsValue CallOnFrameWindow(JsValue frameWindow, string name, in JsCall call) =>
        Realm.Invoke(Realm.GetProperty(frameWindow, name), frameWindow, call.Arguments);
}
