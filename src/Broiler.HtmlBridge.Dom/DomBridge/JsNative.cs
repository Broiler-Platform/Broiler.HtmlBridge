using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge;

public sealed partial class DomBridge
{
    private static readonly JSFunctionDelegate ReturnUndefinedDelegate = ReturnUndefined;
    private static readonly JSFunctionDelegate ReturnNullDelegate = ReturnNull;
    private static readonly JSFunctionDelegate ReturnTrueDelegate = ReturnTrue;
    private static readonly JSFunctionDelegate ReturnZeroDelegate = ReturnZero;

    internal static JSFunction UndefinedFunction(string name, int length = 0) => new(ReturnUndefinedDelegate, name, length);

    internal static JSFunction NullFunction(string name, int length = 0) => new(ReturnNullDelegate, name, length);

    internal static JSFunction TrueFunction(string name, int length = 0) => new(ReturnTrueDelegate, name, length);

    internal static JSFunction ZeroFunction(string name, int length = 0) => new(ReturnZeroDelegate, name, length);

    private static JSValue ReturnUndefined(in Arguments _) => JSUndefined.Value;

    private static JSValue ReturnNull(in Arguments _) => JSNull.Value;

    private static JSValue ReturnTrue(in Arguments _) => JSBoolean.True;

    private static JSValue ReturnZero(in Arguments _) => new JSNumber(0);
}
