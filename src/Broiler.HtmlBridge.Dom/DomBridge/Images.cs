using System.Runtime.CompilerServices;
using Broiler.Dom;
using Broiler.Graphics.Imaging;
using Broiler.JSeal;
using Broiler.Net.Http;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

/// <summary>Image requests owned by DOM elements, including detached images created by Image().</summary>
public sealed partial class DomBridge
{
    private readonly ConditionalWeakTable<DomElement, ImageState> _images = new();
    private readonly Func<byte[], BBitmap> _decodeImage;

    private sealed class ImageState
    {
        public int Generation;
        public bool Complete = true;
        public bool Broken;
        public bool OriginClean = true;
        public BBitmap? Bitmap;
    }

    private static bool IsImageElement(DomElement element) =>
        IsHtmlNamespace(element) && element.TagName.Equals("img", StringComparison.OrdinalIgnoreCase);

    private void RegisterImageConstructor(JsValue window)
    {
        var constructor = Realm.NewConstructor("Image", (in call) =>
        {
            if (call.NewTarget.IsMissing)
                throw call.Realm.Error(JsErrorKind.TypeError, "Image must be constructed with new.");
            var element = NoteCreatedByScript(CreateBridgeElement("img"));
            if (call.Length > 0)
                SetAttr(element, "width", ToImageDimension(call.Realm.ToNumber(call[0])).ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (call.Length > 1)
                SetAttr(element, "height", ToImageDimension(call.Realm.ToNumber(call[1])).ToString(System.Globalization.CultureInfo.InvariantCulture));
            return WrapNode(element);
        }, 0);
        var prototype = PrototypeHandleOfInterface("HTMLImageElement");
        if (prototype.IsObject)
            Realm.SetProperty(constructor, "prototype", prototype);
        DefineWindowGlobal(window, "Image", constructor);
    }

    private static uint ToImageDimension(double value)
    {
        if (!double.IsFinite(value)) return 0;
        var integer = Math.Truncate(value) % 4294967296d;
        return (uint)(integer < 0 ? integer + 4294967296d : integer);
    }

    private ImageState ImageStateFor(DomElement element)
    {
        if (_images.TryGetValue(element, out var state)) return state;
        state = new ImageState();
        _images.Add(element, state);
        if (HasAttr(element, "src")) UpdateImage(element);
        return state;
    }

    private void InstallImageState(JsValue wrapper, DomElement element)
    {
        var state = ImageStateFor(element);
        Realm.DefineAccessor(wrapper, "complete", (in _) => JsValue.Boolean(state.Complete), null);
        Realm.DefineAccessor(wrapper, "naturalWidth", (in _) => JsValue.Number(state.Bitmap?.Width ?? 0), null);
        Realm.DefineAccessor(wrapper, "naturalHeight", (in _) => JsValue.Number(state.Bitmap?.Height ?? 0), null);
        Realm.DefineAccessor(wrapper, "crossOrigin",
            (in _) => GetAttr(element, "crossorigin") is { } value ? JsValue.String(value) : JsValue.Null,
            (in call) =>
            {
                if (call[0].IsNullish) element.RemoveAttribute("crossorigin");
                else SetAttr(element, "crossorigin", call.Realm.ToJsString(call[0]));
                return JsValue.Undefined;
            });
    }

    private JsValue ImageDimension(DomElement element, string dimension)
    {
        var bitmap = ImageStateFor(element).Bitmap;
        return Dom.Features.ComputedStyleBinding.GetUsedDimension(this, dimension, element,
            bitmap is null ? 0 : dimension == "width" ? bitmap.Width : bitmap.Height);
    }

    private void UpdateImage(DomElement element)
    {
        if (_disposed) return;
        var state = _images.GetValue(element, static _ => new ImageState());
        int generation = ++state.Generation;
        state.Bitmap = null;
        state.Broken = false;
        var source = GetAttr(element, "src");
        state.Complete = string.IsNullOrWhiteSpace(source);
        if (source is null) return;

        var document = GetOwningDocument(element);
        var frame = GetFrameForContentDocument(document);
        var ownerWindow = frame is not null && _browsingContexts.TryGetSubWindow(frame, out var subWindow)
            ? subWindow : WindowHandle;
        var client = DocumentContextFor(element);
        var request = RequestContext.Subresource(client, RequestDestination.Image,
            CorsSettings.Parse(GetAttr(element, "crossorigin")));
        var baseUrl = frame is null ? DocumentBaseUrl() : GetSubDocumentBaseUrl(frame);
        var url = Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) && Uri.TryCreate(baseUri, source, out var resolved)
            ? resolved.AbsoluteUri : source;
        var timing = new ResourceTimingRequest("img", client, ResourceTimingSink);

        // Like inserted scripts, loading is a due-now host task. The load window sees the work,
        // listeners can be assigned after src, and no JavaScript runs on a worker thread.
        _eventLoop.QueueTask(() =>
        {
            if (!IsCurrent()) return;
            string eventType;
            try
            {
                if (string.IsNullOrWhiteSpace(source)) throw new InvalidDataException("Empty image source.");
                var resource = _resources.LoadImage(url, request, timing);
                var bitmap = _decodeImage(resource.Bytes);
                if (!IsCurrent()) { bitmap.Dispose(); return; }
                state.Bitmap = bitmap;
                state.OriginClean = resource.OriginClean;
                eventType = "load";
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                if (!IsCurrent()) return;
                state.Broken = true;
                eventType = "error";
            }
            state.Complete = true;
            NoteRenderStateChange();
            // Materialize inline handlers before dispatch; all event forms use the normal target.
            RunWithWindowContext(ownerWindow, () =>
            {
                WrapNode(element);
                var evt = Realm.NewObject();
                Realm.DefineValue(evt, "type", JsValue.String(eventType));
                Realm.DefineValue(evt, "bubbles", JsValue.False);
                _eventDispatch.DispatchEventOnElement(element, evt);
            });
        });

        bool IsCurrent() => !_disposed && _images.TryGetValue(element, out var current) &&
            ReferenceEquals(current, state) && generation == state.Generation &&
            ReferenceEquals(document, GetOwningDocument(element)) &&
            (frame is null || TryGetDocumentContext(document, out _));
    }

    Dom.Features.CanvasImageSource Dom.Features.ICanvasHost.ImageSource(JsValue value)
    {
        if (!value.IsObject || !_jsObjects.TryGetNode(value, out var node) || node is not DomElement element)
            throw Realm.Error(JsErrorKind.TypeError, "drawImage requires an image or canvas element.");
        if (IsImageElement(element))
        {
            var state = ImageStateFor(element);
            if (state.Broken) throw Realm.DomError("InvalidStateError", "The image is broken.");
            return new(state.Bitmap, state.OriginClean);
        }
        if (IsHtmlNamespace(element) && element.TagName.Equals("canvas", StringComparison.OrdinalIgnoreCase))
            return Dom.Features.CanvasBinding.ImageSource(element);
        throw Realm.Error(JsErrorKind.TypeError, "Unsupported drawImage source.");
    }
}
