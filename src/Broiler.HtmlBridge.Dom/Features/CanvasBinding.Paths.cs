using System.Runtime.CompilerServices;
using Broiler.CSS;
using Broiler.Graphics.Color;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

internal static partial class CanvasBinding
{
    private static readonly ConditionalWeakTable<object, CanvasGradientPaint> Gradients = new();

    private static JsValue SetPaint(CanvasRenderingContext2D state, bool stroke, in JsCall call)
    {
        if (call.Length == 0) return JsValue.Undefined;
        CanvasGradientPaint? gradient = null;
        if (call[0].IsObject && call[0].ObjectIdentity is { } key) Gradients.TryGetValue(key, out gradient);
        if (gradient is not null)
        {
            if (stroke) state.StrokeGradient = gradient; else state.FillGradient = gradient;
        }
        else
        {
            var value = call.Realm.ToJsString(call[0]);
            if (!CssValueParser.TryParseColor(value, out _)) return JsValue.Undefined;
            if (stroke) { state.StrokeGradient = null; state.StrokeStyle = value; }
            else { state.FillGradient = null; state.FillStyle = value; }
        }
        return JsValue.Undefined;
    }

    private static void InstallPaths(IJsRealm realm, JsValue ctx, CanvasRenderingContext2D state)
    {
        realm.DefineMethod(ctx, "createRadialGradient", 6, (in call) => Gradient(true, in call));
        realm.DefineMethod(ctx, "createLinearGradient", 4, (in call) => Gradient(false, in call));
        realm.DefineMethod(ctx, "ellipse", 7, (in call) =>
        {
            var v = Numbers(in call, 7);
            if (v.All(double.IsFinite))
            {
                if (v[2] < 0 || v[3] < 0) throw call.Realm.DomError("IndexSizeError", "Ellipse radii must not be negative.");
                state.Ellipse(v[0], v[1], v[2], v[3], v[4], v[5], v[6], call.Length > 7 && call.Realm.ToBoolean(call[7]));
            }
            return JsValue.Undefined;
        });
        realm.DefineMethod(ctx, "bezierCurveTo", 6, (in call) =>
        {
            var v = Numbers(in call, 6);
            if (v.All(double.IsFinite)) state.Bezier(v[0], v[1], v[2], v[3], v[4], v[5]);
            return JsValue.Undefined;
        });
        realm.DefineMethod(ctx, "quadraticCurveTo", 4, (in call) =>
        {
            var v = Numbers(in call, 4);
            if (v.All(double.IsFinite)) state.Quadratic(v[0], v[1], v[2], v[3]);
            return JsValue.Undefined;
        });
    }

    private static double[] Numbers(in JsCall call, int count)
    {
        if (call.Length < count) throw call.Realm.Error(JsErrorKind.TypeError, $"{count} arguments required.");
        var values = new double[count];
        for (var i = 0; i < count; i++) values[i] = call.Realm.ToNumber(call[i]);
        return values;
    }

    private static JsValue Gradient(bool radial, in JsCall call)
    {
        var v = Numbers(in call, radial ? 6 : 4);
        if (!v.All(double.IsFinite)) throw call.Realm.Error(JsErrorKind.TypeError, "Gradient coordinates must be finite.");
        if (radial && (v[2] < 0 || v[5] < 0)) throw call.Realm.DomError("IndexSizeError", "Gradient radii must not be negative.");
        var paint = radial ? new CanvasGradientPaint(v[0], v[1], v[2], v[3], v[4], v[5], true)
            : new CanvasGradientPaint(v[0], v[1], 0, v[2], v[3], 0, false);
        var result = call.Realm.NewObject();
        paint.Handle = result;
        Gradients.Add(result.ObjectIdentity!, paint);
        call.Realm.DefineMethod(result, "addColorStop", 2, (in stop) =>
        {
            if (!stop.This.IsObject || stop.This.ObjectIdentity is not { } key || !Gradients.TryGetValue(key, out var target))
                throw stop.Realm.Error(JsErrorKind.TypeError, "Illegal invocation");
            var offset = stop.Realm.ToNumber(stop[0]);
            if (!double.IsFinite(offset) || offset < 0 || offset > 1) throw stop.Realm.DomError("IndexSizeError", "Offset must be between zero and one.");
            if (!CssValueParser.TryParseColor(stop.Realm.ToJsString(stop[1]), out var color))
                throw stop.Realm.DomError("SyntaxError", "Invalid color.");
            target.Add(offset, new BColor(color.Red, color.Green, color.Blue, color.Alpha));
            return JsValue.Undefined;
        });
        return result;
    }
}
