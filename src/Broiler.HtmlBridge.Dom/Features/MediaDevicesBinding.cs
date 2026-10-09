using System;
using System.Runtime.CompilerServices;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// State for <c>MediaDevices</c> (Media Capture and Streams §9).
/// </summary>
internal sealed class MediaDevicesState
{
    public JsValue OnDeviceChange { get; set; } = JsValue.Null;
}

/// <summary>
/// <c>MediaDevices</c> and <c>MediaDeviceInfo</c> interfaces (W3C Media Capture and Streams).
/// </summary>
internal static class MediaDevicesBinding
{
    private static readonly ConditionalWeakTable<object, MediaDevicesState> MediaDevicesInstances = new();

    private static object IdentityOf(JsValue value) =>
        value.ObjectIdentity ?? throw new InvalidOperationException(
            "a per-object registry was keyed on a handle that is not an object");

    public static void EnsureInterfaces(IJsRealm realm, JsValue? window = null)
    {
        var existing = realm.GetProperty(realm.Global, "MediaDevices");
        if (existing.IsObject)
        {
            if (window.HasValue && window.Value.IsObject && realm.GetProperty(window.Value, "MediaDevices").IsMissing)
            {
                realm.SetProperty(window.Value, "MediaDevices", existing);
                realm.SetProperty(window.Value, "MediaDeviceInfo", realm.GetProperty(realm.Global, "MediaDeviceInfo"));
            }
            return;
        }

        realm.EvaluateHostScript("""
            (function () {
                function MediaDevices() { throw new TypeError("Failed to construct 'MediaDevices': Illegal constructor"); }
                function MediaDeviceInfo() { throw new TypeError("Failed to construct 'MediaDeviceInfo': Illegal constructor"); }

                if (typeof EventTarget === 'function' && EventTarget.prototype) {
                    Object.setPrototypeOf(MediaDevices, EventTarget);
                    Object.setPrototypeOf(MediaDevices.prototype, EventTarget.prototype);
                }

                Object.defineProperty(MediaDevices.prototype, Symbol.toStringTag, {
                    value: 'MediaDevices', writable: false, enumerable: false, configurable: true
                });
                Object.defineProperty(MediaDeviceInfo.prototype, Symbol.toStringTag, {
                    value: 'MediaDeviceInfo', writable: false, enumerable: false, configurable: true
                });

                globalThis.MediaDevices = MediaDevices;
                globalThis.MediaDeviceInfo = MediaDeviceInfo;
            })();
            """, "interfaces:mediadevices");

        var mediaDevicesCtor = realm.GetProperty(realm.Global, "MediaDevices");
        var mediaDeviceInfoCtor = realm.GetProperty(realm.Global, "MediaDeviceInfo");

        if (window.HasValue && window.Value.IsObject)
        {
            if (mediaDevicesCtor.IsObject)
                realm.SetProperty(window.Value, "MediaDevices", mediaDevicesCtor);
            if (mediaDeviceInfoCtor.IsObject)
                realm.SetProperty(window.Value, "MediaDeviceInfo", mediaDeviceInfoCtor);
        }

        if (mediaDevicesCtor.IsObject)
        {
            var prototype = realm.GetProperty(mediaDevicesCtor, "prototype");
            if (prototype.IsObject)
            {
                InstallMediaDevicesPrototype(realm, prototype);
            }
        }

        if (mediaDeviceInfoCtor.IsObject)
        {
            var prototype = realm.GetProperty(mediaDeviceInfoCtor, "prototype");
            if (prototype.IsObject)
            {
                InstallMediaDeviceInfoPrototype(realm, prototype);
            }
        }
    }

    public static JsValue BuildMediaDevices(IJsRealm realm, JsValue? window = null)
    {
        EnsureInterfaces(realm, window);
        var ctor = realm.GetProperty(realm.Global, "MediaDevices");
        var proto = ctor.IsObject ? realm.GetProperty(ctor, "prototype") : JsValue.Undefined;

        var instance = realm.NewObject();
        if (proto.IsObject)
        {
            realm.SetPrototype(instance, proto);
        }

        MediaDevicesInstances.AddOrUpdate(IdentityOf(instance), new MediaDevicesState());
        return instance;
    }

    private static void InstallMediaDevicesPrototype(IJsRealm realm, JsValue prototype)
    {
        realm.DefineAccessor(
            prototype,
            "ondevicechange",
            static (in call) =>
            {
                var state = StateForGetter(in call, "ondevicechange");
                return state.OnDeviceChange;
            },
            static (in call) =>
            {
                var state = StateForSetter(in call, "ondevicechange");
                state.OnDeviceChange = call.Length > 0 ? call[0] : JsValue.Null;
                return JsValue.Undefined;
            });

        realm.DefineMethod(prototype, "enumerateDevices", 0, static (in call) =>
        {
            StateForMethod(in call, "enumerateDevices");
            var promise = call.Realm.NewPromise(out var resolve, out _);
            resolve(call.Realm.NewArray());
            return promise;
        });

        realm.DefineMethod(prototype, "getUserMedia", 0, static (in call) =>
        {
            StateForMethod(in call, "getUserMedia");
            var promise = call.Realm.NewPromise(out _, out var reject);
            var error = BuildDomException(call.Realm, "Requested device not found", "NotFoundError");
            reject(error);
            return promise;
        });

        realm.DefineMethod(prototype, "getDisplayMedia", 0, static (in call) =>
        {
            StateForMethod(in call, "getDisplayMedia");
            var promise = call.Realm.NewPromise(out _, out var reject);
            var error = BuildDomException(call.Realm, "Permission denied", "NotAllowedError");
            reject(error);
            return promise;
        });

        realm.DefineMethod(prototype, "getSupportedConstraints", 0, static (in call) =>
        {
            StateForMethod(in call, "getSupportedConstraints");
            var constraints = call.Realm.NewObject();
            string[] supported =
            [
                "aspectRatio",
                "autoGainControl",
                "channelCount",
                "deviceId",
                "displaySurface",
                "echoCancellation",
                "facingMode",
                "frameRate",
                "groupId",
                "height",
                "noiseSuppression",
                "sampleRate",
                "sampleSize",
                "width"
            ];
            foreach (var name in supported)
            {
                call.Realm.DefineValue(constraints, name, JsValue.True);
            }
            return constraints;
        });
    }

    private static void InstallMediaDeviceInfoPrototype(IJsRealm realm, JsValue prototype)
    {
        string[] attrs = ["deviceId", "groupId", "kind", "label"];
        foreach (var attr in attrs)
        {
            var name = attr;
            realm.DefineAccessor(
                prototype,
                name,
                static (in call) =>
                {
                    throw call.Realm.Error(
                        JsErrorKind.TypeError,
                        "Illegal invocation");
                },
                null);
        }

        realm.DefineMethod(prototype, "toJSON", 0, static (in call) =>
        {
            throw call.Realm.Error(
                JsErrorKind.TypeError,
                "Illegal invocation");
        });
    }

    internal static JsValue BuildDomException(IJsRealm realm, string message, string name)
    {
        var constructor = realm.GetProperty(realm.Global, "DOMException");
        return constructor.IsFunction
            ? realm.Construct(constructor, [JsValue.String(message), JsValue.String(name)])
            : JsValue.String($"DOMException: {message} ({name})");
    }

    private static MediaDevicesState StateForGetter(in JsCall call, string member)
    {
        if (call.This.IsObject && call.This.ObjectIdentity is { } id && MediaDevicesInstances.TryGetValue(id, out var state))
            return state;

        throw call.Realm.Error(
            JsErrorKind.TypeError,
            $"Failed to read the '{member}' property from 'MediaDevices': Illegal invocation");
    }

    private static MediaDevicesState StateForSetter(in JsCall call, string member)
    {
        if (call.This.IsObject && call.This.ObjectIdentity is { } id && MediaDevicesInstances.TryGetValue(id, out var state))
            return state;

        throw call.Realm.Error(
            JsErrorKind.TypeError,
            $"Failed to set the '{member}' property on 'MediaDevices': Illegal invocation");
    }

    private static MediaDevicesState StateForMethod(in JsCall call, string member)
    {
        if (call.This.IsObject && call.This.ObjectIdentity is { } id && MediaDevicesInstances.TryGetValue(id, out var state))
            return state;

        throw call.Realm.Error(
            JsErrorKind.TypeError,
            $"Failed to execute '{member}' on 'MediaDevices': Illegal invocation");
    }
}
