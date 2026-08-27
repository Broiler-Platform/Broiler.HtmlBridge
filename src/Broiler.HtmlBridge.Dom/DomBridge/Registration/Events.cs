using Broiler.JavaScript.Engine;

namespace Broiler.HtmlBridge;

public sealed partial class DomBridge
{
    private void RegisterDocumentEventsAndMutationObservers(JSContext context)
    {
        // Event / typed event constructors — DOM Level 4
        context.Eval(@"
                function Event(type, options) {
                    options = options || {};
                    var evt = document.createEvent('Event');
                    evt.initEvent(type, options.bubbles === true, options.cancelable === true);
                    return evt;
                }

                function CustomEvent(type, options) {
                    options = options || {};
                    var evt = document.createEvent('CustomEvent');
                    evt.initCustomEvent(
                        type,
                        options.bubbles === true,
                        options.cancelable === true,
                        options.detail !== undefined ? options.detail : null);
                    return evt;
                }

                function MouseEvent(type, options) {
                    options = options || {};
                    var evt = document.createEvent('MouseEvents');
                    evt.initMouseEvent(
                        type,
                        options.bubbles === true,
                        options.cancelable === true,
                        options.view !== undefined ? options.view : null,
                        options.detail !== undefined ? options.detail : 0,
                        options.screenX !== undefined ? options.screenX : 0,
                        options.screenY !== undefined ? options.screenY : 0,
                        options.clientX !== undefined ? options.clientX : 0,
                        options.clientY !== undefined ? options.clientY : 0,
                        options.ctrlKey === true,
                        options.altKey === true,
                        options.shiftKey === true,
                        options.metaKey === true,
                        options.button !== undefined ? options.button : 0,
                        options.relatedTarget !== undefined ? options.relatedTarget : null);
                    return evt;
                }

                function FocusEvent(type, options) {
                    options = options || {};
                    var evt = document.createEvent('FocusEvents');
                    evt.initFocusEvent(
                        type,
                        options.bubbles === true,
                        options.cancelable === true,
                        options.view !== undefined ? options.view : null,
                        options.detail !== undefined ? options.detail : 0,
                        options.relatedTarget !== undefined ? options.relatedTarget : null);
                    return evt;
                }

                function KeyboardEvent(type, options) {
                    options = options || {};
                    var evt = document.createEvent('KeyboardEvents');
                    evt.initKeyboardEvent(
                        type,
                        options.bubbles === true,
                        options.cancelable === true,
                        options.view !== undefined ? options.view : null,
                        options.key !== undefined ? options.key : '',
                        options.location !== undefined ? options.location : 0,
                        options.ctrlKey === true,
                        options.altKey === true,
                        options.shiftKey === true,
                        options.metaKey === true,
                        options.repeat === true,
                        options.keyCode !== undefined ? options.keyCode : 0,
                        options.charCode !== undefined ? options.charCode : 0);
                    return evt;
                }

                function WheelEvent(type, options) {
                    options = options || {};
                    var evt = document.createEvent('WheelEvents');
                    var modifiers = [];
                    if (options.ctrlKey === true) modifiers.push('Control');
                    if (options.altKey === true) modifiers.push('Alt');
                    if (options.shiftKey === true) modifiers.push('Shift');
                    if (options.metaKey === true) modifiers.push('Meta');
                    evt.initWheelEvent(
                        type,
                        options.bubbles === true,
                        options.cancelable === true,
                        options.view !== undefined ? options.view : null,
                        options.detail !== undefined ? options.detail : 0,
                        options.screenX !== undefined ? options.screenX : 0,
                        options.screenY !== undefined ? options.screenY : 0,
                        options.clientX !== undefined ? options.clientX : 0,
                        options.clientY !== undefined ? options.clientY : 0,
                        options.button !== undefined ? options.button : 0,
                        options.relatedTarget !== undefined ? options.relatedTarget : null,
                        modifiers.join(' '),
                        options.deltaX !== undefined ? options.deltaX : 0,
                        options.deltaY !== undefined ? options.deltaY : 0,
                        options.deltaZ !== undefined ? options.deltaZ : 0,
                        options.deltaMode !== undefined ? options.deltaMode : 0);
                    return evt;
                }

                function UIEvent(type, options) {
                    options = options || {};
                    var evt = document.createEvent('UIEvents');
                    evt.initUIEvent(
                        type,
                        options.bubbles === true,
                        options.cancelable === true,
                        options.view !== undefined ? options.view : null,
                        options.detail !== undefined ? options.detail : 0);
                    return evt;
                }

                function InputEvent(type, options) {
                    options = options || {};
                    var evt = document.createEvent('InputEvent');
                    evt.initInputEvent(
                        type,
                        options.bubbles === true,
                        options.cancelable === true,
                        options.view !== undefined ? options.view : null,
                        options.data !== undefined ? options.data : null,
                        options.inputType !== undefined ? options.inputType : '',
                        options.isComposing === true);
                    return evt;
                }
            ");
        // MutationObserver (constructor/prototype + host bridge functions) is installed by the
        // Phase 3 MutationObserverBinding feature module.
        _mutations.RegisterDocumentApis(context);
    }

}
