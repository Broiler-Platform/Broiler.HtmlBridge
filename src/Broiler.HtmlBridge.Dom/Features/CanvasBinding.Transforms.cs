using Broiler.Graphics.Geometry;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

internal static partial class CanvasBinding
{
    private static void InstallTransforms(IJsRealm realm, JsValue ctx, CanvasRenderingContext2D state)
    {
        realm.DefineMethod(ctx, "getTransform", 0, (in call) =>
        {
            // Independent affine snapshot; later matrix mutations do not change the context.
            var m = state.Transform;
            var result = call.Realm.NewObject();
            var names = new[] { "a", "b", "c", "d", "e", "f" };
            var values = new[] { m.M11, m.M12, m.M21, m.M22, m.M31, m.M32 };
            for (var i = 0; i < names.Length; i++) call.Realm.DefineValue(result, names[i], JsValue.Number(values[i]));
            call.Realm.DefineValue(result, "is2D", JsValue.Boolean(true));
            call.Realm.DefineValue(result, "isIdentity", JsValue.Boolean(m.IsIdentity));
            return result;
        });
        realm.DefineMethod(ctx, "resetTransform", 0, (in _) => { state.Transform = BMatrix3x2.Identity; return JsValue.Undefined; });
        realm.DefineMethod(ctx, "setTransform", 0, (in call) =>
        {
            double[] v;
            if (call.Length == 0) v = [1, 0, 0, 1, 0, 0];
            else if (call.Length == 1 && call[0].IsObject)
            {
                var names = new[] { "a", "b", "c", "d", "e", "f" };
                v = [1, 0, 0, 1, 0, 0];
                for (var i = 0; i < names.Length; i++)
                {
                    var value = call.Realm.GetProperty(call[0], names[i]);
                    if (!value.IsUndefined && !value.IsMissing) v[i] = call.Realm.ToNumber(value);
                }
            }
            else v = Numbers(in call, 6);
            if (v.All(double.IsFinite)) state.Transform = new(v[0], v[1], v[2], v[3], v[4], v[5]);
            return JsValue.Undefined;
        });
        realm.DefineMethod(ctx, "transform", 6, (in call) =>
        {
            var v = Numbers(in call, 6);
            if (v.All(double.IsFinite)) state.Concatenate(new(v[0], v[1], v[2], v[3], v[4], v[5]));
            return JsValue.Undefined;
        });
        realm.DefineMethod(ctx, "translate", 2, (in call) =>
        {
            var v = Numbers(in call, 2);
            if (v.All(double.IsFinite)) state.Concatenate(BMatrix3x2.Translation(v[0], v[1]));
            return JsValue.Undefined;
        });
        realm.DefineMethod(ctx, "scale", 2, (in call) =>
        {
            var v = Numbers(in call, 2);
            if (v.All(double.IsFinite)) state.Concatenate(BMatrix3x2.Scale(v[0], v[1]));
            return JsValue.Undefined;
        });
        realm.DefineMethod(ctx, "rotate", 1, (in call) =>
        {
            var v = Numbers(in call, 1)[0];
            if (double.IsFinite(v)) state.Concatenate(new(Math.Cos(v), Math.Sin(v), -Math.Sin(v), Math.Cos(v), 0, 0));
            return JsValue.Undefined;
        });
    }
}
