using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// State for <c>DOMRectReadOnly</c> and <c>DOMRect</c> (CSSOM View §4).
/// </summary>
internal sealed class DomRectState
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool IsReadOnly { get; }

    public DomRectState(double x, double y, double width, double height, bool isReadOnly)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
        IsReadOnly = isReadOnly;
    }

    public double Top => Height >= 0 ? Y : Y + Height;
    public double Bottom => Height >= 0 ? Y + Height : Y;
    public double Left => Width >= 0 ? X : X + Width;
    public double Right => Width >= 0 ? X + Width : X;
}

/// <summary>
/// State for <c>DOMRectList</c> (CSSOM View §5).
/// </summary>
internal sealed class DomRectListState
{
    public IReadOnlyList<JsValue> Rects { get; }

    public DomRectListState(IReadOnlyList<JsValue> rects)
    {
        Rects = rects;
    }
}

/// <summary>
/// Geometry interfaces: <c>DOMRectReadOnly</c>, <c>DOMRect</c>, and <c>DOMRectList</c> (CSSOM View §4 / §5).
/// </summary>
internal static class GeometryBinding
{
    private static readonly ConditionalWeakTable<object, DomRectState> RectStates = new();
    private static readonly ConditionalWeakTable<object, DomRectListState> ListStates = new();

    private static object IdentityOf(JsValue value) =>
        value.ObjectIdentity ?? throw new InvalidOperationException(
            "a per-object registry was keyed on a handle that is not an object");

    public static void Install(IJsRealm realm, JsValue window, bool isWorker = false)
    {
        EnsureInterfaces(realm, window, isWorker);
    }

    public static void EnsureInterfaces(IJsRealm realm, JsValue window, bool isWorker = false)
    {
        var existing = realm.GetProperty(realm.Global, "DOMRect");
        if (existing.IsObject)
        {
            if (window.IsObject && realm.GetProperty(window, "DOMRect").IsMissing)
            {
                realm.SetProperty(window, "DOMRectReadOnly", realm.GetProperty(realm.Global, "DOMRectReadOnly"));
                realm.SetProperty(window, "DOMRect", existing);
                if (!isWorker)
                    realm.SetProperty(window, "DOMRectList", realm.GetProperty(realm.Global, "DOMRectList"));
            }
            return;
        }

        // ==========================================
        // 1. DOMRectReadOnly (CSSOM View §4.1)
        // ==========================================
        var domRectReadOnlyCtor = realm.NewConstructor("DOMRectReadOnly", (in call) =>
        {
            if (call.NewTarget.IsMissing)
            {
                throw call.Realm.Error(
                    JsErrorKind.TypeError,
                    "Failed to construct 'DOMRectReadOnly': Please use the 'new' operator, this DOM object constructor cannot be called as a function.");
            }

            var x = call.Length > 0 ? call.Realm.ToNumber(call[0]) : 0.0;
            var y = call.Length > 1 ? call.Realm.ToNumber(call[1]) : 0.0;
            var w = call.Length > 2 ? call.Realm.ToNumber(call[2]) : 0.0;
            var h = call.Length > 3 ? call.Realm.ToNumber(call[3]) : 0.0;

            var instance = call.Realm.NewObject();
            var proto = call.Realm.GetProperty(call.NewTarget, "prototype");
            if (proto.IsObject)
                call.Realm.SetPrototype(instance, proto);

            RectStates.Add(IdentityOf(instance), new DomRectState(x, y, w, h, isReadOnly: true));
            return instance;
        }, 0);

        var readOnlyProto = realm.GetProperty(domRectReadOnlyCtor, "prototype");

        realm.DefineAccessor(readOnlyProto, "x", (in call) => GetRectProp(in call, s => s.X, "x"), null);
        realm.DefineAccessor(readOnlyProto, "y", (in call) => GetRectProp(in call, s => s.Y, "y"), null);
        realm.DefineAccessor(readOnlyProto, "width", (in call) => GetRectProp(in call, s => s.Width, "width"), null);
        realm.DefineAccessor(readOnlyProto, "height", (in call) => GetRectProp(in call, s => s.Height, "height"), null);
        realm.DefineAccessor(readOnlyProto, "top", (in call) => GetRectProp(in call, s => s.Top, "top"), null);
        realm.DefineAccessor(readOnlyProto, "right", (in call) => GetRectProp(in call, s => s.Right, "right"), null);
        realm.DefineAccessor(readOnlyProto, "bottom", (in call) => GetRectProp(in call, s => s.Bottom, "bottom"), null);
        realm.DefineAccessor(readOnlyProto, "left", (in call) => GetRectProp(in call, s => s.Left, "left"), null);

        realm.DefineMethod(readOnlyProto, "toJSON", 0, (in call) =>
        {
            if (!call.This.IsObject || !RectStates.TryGetValue(IdentityOf(call.This), out var state))
            {
                throw call.Realm.Error(
                    JsErrorKind.TypeError,
                    "Failed to execute 'toJSON' on 'DOMRectReadOnly': Illegal invocation");
            }

            var obj = call.Realm.NewObject();
            call.Realm.DefineValue(obj, "x", JsValue.Number(state.X));
            call.Realm.DefineValue(obj, "y", JsValue.Number(state.Y));
            call.Realm.DefineValue(obj, "width", JsValue.Number(state.Width));
            call.Realm.DefineValue(obj, "height", JsValue.Number(state.Height));
            call.Realm.DefineValue(obj, "top", JsValue.Number(state.Top));
            call.Realm.DefineValue(obj, "right", JsValue.Number(state.Right));
            call.Realm.DefineValue(obj, "bottom", JsValue.Number(state.Bottom));
            call.Realm.DefineValue(obj, "left", JsValue.Number(state.Left));
            return obj;
        });

        realm.DefineMethod(domRectReadOnlyCtor, "fromRect", 0, (in call) =>
        {
            var (x, y, w, h) = ExtractRectInit(call.Realm, in call);
            return CreateDomRectReadOnly(call.Realm, x, y, w, h);
        });

        SetToStringTag(realm, readOnlyProto, "DOMRectReadOnly");

        // ==========================================
        // 2. DOMRect (CSSOM View §4.2)
        // ==========================================
        var domRectCtor = realm.NewConstructor("DOMRect", (in call) =>
        {
            if (call.NewTarget.IsMissing)
            {
                throw call.Realm.Error(
                    JsErrorKind.TypeError,
                    "Failed to construct 'DOMRect': Please use the 'new' operator, this DOM object constructor cannot be called as a function.");
            }

            var x = call.Length > 0 ? call.Realm.ToNumber(call[0]) : 0.0;
            var y = call.Length > 1 ? call.Realm.ToNumber(call[1]) : 0.0;
            var w = call.Length > 2 ? call.Realm.ToNumber(call[2]) : 0.0;
            var h = call.Length > 3 ? call.Realm.ToNumber(call[3]) : 0.0;

            var instance = call.Realm.NewObject();
            var proto = call.Realm.GetProperty(call.NewTarget, "prototype");
            if (proto.IsObject)
                call.Realm.SetPrototype(instance, proto);

            RectStates.Add(IdentityOf(instance), new DomRectState(x, y, w, h, isReadOnly: false));
            return instance;
        }, 0);

        var rectProto = realm.GetProperty(domRectCtor, "prototype");

        // Inheritance: DOMRect -> DOMRectReadOnly, DOMRect.prototype -> DOMRectReadOnly.prototype
        realm.SetPrototype(domRectCtor, domRectReadOnlyCtor);
        realm.SetPrototype(rectProto, readOnlyProto);

        realm.DefineAccessor(rectProto, "x",
            (in call) => GetRectProp(in call, s => s.X, "x", "DOMRect"),
            (in call) => SetRectProp(in call, (s, v) => s.X = v, "x"));
        realm.DefineAccessor(rectProto, "y",
            (in call) => GetRectProp(in call, s => s.Y, "y", "DOMRect"),
            (in call) => SetRectProp(in call, (s, v) => s.Y = v, "y"));
        realm.DefineAccessor(rectProto, "width",
            (in call) => GetRectProp(in call, s => s.Width, "width", "DOMRect"),
            (in call) => SetRectProp(in call, (s, v) => s.Width = v, "width"));
        realm.DefineAccessor(rectProto, "height",
            (in call) => GetRectProp(in call, s => s.Height, "height", "DOMRect"),
            (in call) => SetRectProp(in call, (s, v) => s.Height = v, "height"));

        realm.DefineMethod(domRectCtor, "fromRect", 0, (in call) =>
        {
            var (x, y, w, h) = ExtractRectInit(call.Realm, in call);
            return CreateDomRect(call.Realm, x, y, w, h);
        });

        SetToStringTag(realm, rectProto, "DOMRect");

        // Publish DOMRectReadOnly and DOMRect
        realm.DefineValue(realm.Global, "DOMRectReadOnly", domRectReadOnlyCtor);
        realm.DefineValue(realm.Global, "DOMRect", domRectCtor);
        if (window.IsObject && !ReferenceEquals(window.ObjectIdentity, realm.Global.ObjectIdentity))
        {
            realm.DefineValue(window, "DOMRectReadOnly", domRectReadOnlyCtor);
            realm.DefineValue(window, "DOMRect", domRectCtor);
        }

        // ==========================================
        // 3. DOMRectList (CSSOM View §5) - Window only
        // ==========================================
        if (!isWorker)
        {
            var domRectListCtor = realm.NewConstructor("DOMRectList", (in call) =>
            {
                throw call.Realm.Error(JsErrorKind.TypeError, "Illegal constructor");
            }, 0);

            var listProto = realm.GetProperty(domRectListCtor, "prototype");

            realm.DefineAccessor(listProto, "length", (in call) =>
            {
                if (!call.This.IsObject || !ListStates.TryGetValue(IdentityOf(call.This), out var state))
                {
                    throw call.Realm.Error(
                        JsErrorKind.TypeError,
                        "Failed to read the 'length' property from 'DOMRectList': Illegal invocation");
                }
                return JsValue.Number(state.Rects.Count);
            }, null);

            realm.DefineMethod(listProto, "item", 1, (in call) =>
            {
                if (!call.This.IsObject || !ListStates.TryGetValue(IdentityOf(call.This), out var state))
                {
                    throw call.Realm.Error(
                        JsErrorKind.TypeError,
                        "Failed to execute 'item' on 'DOMRectList': Illegal invocation");
                }
                if (call.Length == 0) return JsValue.Null;
                var num = call.Realm.ToNumber(call[0]);
                if (double.IsNaN(num) || num < 0 || num >= state.Rects.Count)
                    return JsValue.Null;
                return state.Rects[(int)num];
            });

            SetToStringTag(realm, listProto, "DOMRectList");

            realm.DefineValue(realm.Global, "DOMRectList", domRectListCtor);
            if (window.IsObject && !ReferenceEquals(window.ObjectIdentity, realm.Global.ObjectIdentity))
            {
                realm.DefineValue(window, "DOMRectList", domRectListCtor);
            }

            // Wire iterator onto DOMRectList.prototype for for...of and spread (WebIDL indexed property getter)
            realm.EvaluateHostScript("""
                (function () {
                    try {
                        if (typeof DOMRectList === 'function' && DOMRectList.prototype) {
                            var iter = Array.prototype[Symbol.iterator] || function () {
                                var list = this, i = 0;
                                var it = {
                                    next: function () {
                                        return i < list.length ? { value: list[i++], done: false } : { value: undefined, done: true };
                                    }
                                };
                                it[Symbol.iterator] = function () { return this; };
                                return it;
                            };
                            DOMRectList.prototype[Symbol.iterator] = iter;
                        }
                    } catch (e) {}
                })();
                """, "geometry:domrectlist-iterator");
        }
    }

    private static JsValue GetRectProp(in JsCall call, Func<DomRectState, double> reader, string propName, string interfaceName = "DOMRectReadOnly")
    {
        if (!call.This.IsObject || !RectStates.TryGetValue(IdentityOf(call.This), out var state))
        {
            throw call.Realm.Error(
                JsErrorKind.TypeError,
                $"Failed to read the '{propName}' property from '{interfaceName}': Illegal invocation");
        }
        return JsValue.Number(reader(state));
    }

    private static JsValue SetRectProp(in JsCall call, Action<DomRectState, double> writer, string propName)
    {
        if (!call.This.IsObject || !RectStates.TryGetValue(IdentityOf(call.This), out var state) || state.IsReadOnly)
        {
            throw call.Realm.Error(
                JsErrorKind.TypeError,
                $"Failed to set the '{propName}' property on 'DOMRect': Illegal invocation");
        }
        var value = call.Length > 0 ? call.Realm.ToNumber(call[0]) : double.NaN;
        writer(state, value);
        return JsValue.Undefined;
    }

    private static (double x, double y, double width, double height) ExtractRectInit(IJsRealm realm, in JsCall call)
    {
        if (call.Length == 0 || !call[0].IsObject)
            return (0.0, 0.0, 0.0, 0.0);

        var obj = call[0];
        var x = GetNumericProp(realm, obj, "x", 0.0);
        var y = GetNumericProp(realm, obj, "y", 0.0);
        var w = GetNumericProp(realm, obj, "width", 0.0);
        var h = GetNumericProp(realm, obj, "height", 0.0);
        return (x, y, w, h);
    }

    private static double GetNumericProp(IJsRealm realm, JsValue obj, string name, double defaultValue)
    {
        var val = realm.GetProperty(obj, name);
        if (val.IsMissing || val.IsUndefined)
            return defaultValue;
        return realm.ToNumber(val);
    }

    private static void SetToStringTag(IJsRealm realm, JsValue prototype, string tag)
    {
        var fn = realm.EvaluateHostScript($$"""
            (function (p) {
                Object.defineProperty(p, Symbol.toStringTag, {
                    value: '{{tag}}', writable: false, enumerable: false, configurable: true
                });
            })
            """, "geometry:toStringTag");
        if (fn.IsFunction)
            realm.Invoke(fn, JsValue.Undefined, [prototype]);
    }

    public static JsValue CreateDomRect(IJsRealm realm, double x, double y, double width, double height)
    {
        EnsureInterfaces(realm, realm.Global);
        var rectCtor = realm.GetProperty(realm.Global, "DOMRect");
        var rectProto = realm.GetProperty(rectCtor, "prototype");
        var instance = realm.NewObject();
        if (rectProto.IsObject)
            realm.SetPrototype(instance, rectProto);

        RectStates.Add(IdentityOf(instance), new DomRectState(x, y, width, height, isReadOnly: false));
        return instance;
    }

    public static JsValue CreateDomRectReadOnly(IJsRealm realm, double x, double y, double width, double height)
    {
        EnsureInterfaces(realm, realm.Global);
        var readOnlyCtor = realm.GetProperty(realm.Global, "DOMRectReadOnly");
        var proto = realm.GetProperty(readOnlyCtor, "prototype");
        var instance = realm.NewObject();
        if (proto.IsObject)
            realm.SetPrototype(instance, proto);

        RectStates.Add(IdentityOf(instance), new DomRectState(x, y, width, height, isReadOnly: true));
        return instance;
    }

    public static JsValue CreateDomRectList(IJsRealm realm, IReadOnlyList<(double Left, double Top, double Width, double Height)> rects)
    {
        EnsureInterfaces(realm, realm.Global);
        var listCtor = realm.GetProperty(realm.Global, "DOMRectList");
        var proto = realm.GetProperty(listCtor, "prototype");
        var instance = realm.NewObject();
        if (proto.IsObject)
            realm.SetPrototype(instance, proto);

        var jsRects = new JsValue[rects.Count];
        for (int i = 0; i < rects.Count; i++)
        {
            var (left, top, width, height) = rects[i];
            var jsRect = CreateDomRect(realm, left, top, width, height);
            jsRects[i] = jsRect;
            realm.DefineValue(instance, i.ToString(CultureInfo.InvariantCulture), jsRect);
        }

        ListStates.Add(IdentityOf(instance), new DomRectListState(jsRects));
        return instance;
    }

    public static JsValue CreateDomRectList(IJsRealm realm, IReadOnlyList<JsValue> rectObjects)
    {
        EnsureInterfaces(realm, realm.Global);
        var listCtor = realm.GetProperty(realm.Global, "DOMRectList");
        var proto = realm.GetProperty(listCtor, "prototype");
        var instance = realm.NewObject();
        if (proto.IsObject)
            realm.SetPrototype(instance, proto);

        for (int i = 0; i < rectObjects.Count; i++)
        {
            realm.DefineValue(instance, i.ToString(CultureInfo.InvariantCulture), rectObjects[i]);
        }

        ListStates.Add(IdentityOf(instance), new DomRectListState(rectObjects));
        return instance;
    }
}
