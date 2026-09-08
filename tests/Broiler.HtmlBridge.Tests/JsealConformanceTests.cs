using System.Numerics;
using System.Reflection;

using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.Browser.Core.Tests;

/// <summary>
/// What "a correct JSEAL provider" means, asserted against the contracts and nothing else.
/// <para>
/// Every test here is a <see cref="TheoryAttribute"/> over <see cref="Engines"/>, which is
/// <see cref="JsEngineRegistry.All"/>. Today that is one provider and the suite reads as an
/// ordinary test class; the reason it is shaped this way is the second one. Adding an engine adds
/// a registration and no test code, and the definition of done for that engine is already written
/// down here — which is the thing a conformance suite buys and a per-engine test class does not.
/// </para>
/// <para>
/// <b>Nothing in this file names an engine type.</b> The assertions are all on JSEAL contracts, so
/// a provider that passes has demonstrated the behaviour the DOM bridge binds against rather than
/// the behaviour its own engine happens to have. Where a contract leaves an outcome open — what an
/// assignment to a setter-less accessor does, what order an exotic object enumerates in — the test
/// pins what the provider actually does and says so in a comment, because an unpinned outcome is
/// one a second provider would be free to differ on silently.
/// </para>
/// </summary>
public class JsealConformanceTests
{
    /// <summary>
    /// Registration is explicit rather than left to assembly load.
    /// </summary>
    /// <remarks>
    /// A provider registers itself from a <c>[ModuleInitializer]</c>, which the CLR runs when the
    /// assembly is first loaded — and it loads an assembly when a type in it is first touched. This
    /// suite touches no engine type at all, by design, so nothing here would drag the provider
    /// assembly in and <see cref="Engines"/> could enumerate an empty registry. Naming
    /// <see cref="JsEngineHosting"/> — the host wiring step, which references every provider this
    /// build linked — is what makes the reference real. It is idempotent.
    /// </remarks>
    static JsealConformanceTests() => JsEngineHosting.EnsureRegistered();

    /// <summary>
    /// Every registered provider, by name.
    /// </summary>
    /// <remarks>
    /// The name rather than the provider instance so that a failing case names the engine it failed
    /// for — xUnit renders theory data into the test name, and an <see cref="IJsEngineProvider"/>
    /// renders as its type name at best. Reading a static member of this class is also what runs the
    /// type initializer above, so the registry is populated before it is enumerated.
    /// </remarks>
    public static IEnumerable<object[]> Engines =>
        JsEngineRegistry.All.Select(provider => new object[] { provider.Name });

    private static IJsEngineProvider Provider(string engine)
    {
        var provider = JsEngineRegistry.Find(engine);
        Assert.NotNull(provider);
        return provider!;
    }

    private static IJsRealm NewRealm(string engine, JsRealmOptions? options = null) =>
        Provider(engine).CreateRealm(options ?? JsRealmOptions.Default);

    /// <summary>Whether a realm lacks a capability, for the tests that need one.</summary>
    private static bool Lacks(IJsRealm realm, JsCapabilities capability) =>
        (realm.Capabilities & capability) == 0;

    /// <summary>
    /// Runs <paramref name="expression"/> as host script and answers it as a string, which is how
    /// most of the from-JavaScript assertions below read their result.
    /// </summary>
    private static string Eval(IJsRealm realm, string expression, string label = "test:probe") =>
        realm.ToJsString(realm.EvaluateHostScript(expression, label));

    // ── values ─────────────────────────────────────────────────────────────────────────────────
    //
    // JsValue is a struct in the contract assembly with no provider involvement — a handle's kind,
    // its truthiness and its equality are decided by the contracts alone, which is the whole reason
    // those members exist rather than being realm calls. So these are Facts: running them once per
    // provider would assert the same code N times. The two value questions that DO need an engine —
    // whether an object is truthy, and whether two object handles compare by identity — are
    // theories further down, because minting an object is the provider's job.

    [Fact]
    public void EveryValueFactoryAnswersItsOwnKind()
    {
        Assert.Equal(JsValueKind.Missing, JsValue.Missing.Kind);
        Assert.Equal(JsValueKind.Undefined, JsValue.Undefined.Kind);
        Assert.Equal(JsValueKind.Null, JsValue.Null.Kind);
        Assert.Equal(JsValueKind.Boolean, JsValue.True.Kind);
        Assert.Equal(JsValueKind.Boolean, JsValue.False.Kind);
        Assert.Equal(JsValueKind.Boolean, JsValue.Boolean(false).Kind);
        Assert.Equal(JsValueKind.Number, JsValue.Number(0d).Kind);
        Assert.Equal(JsValueKind.String, JsValue.String("s").Kind);

        // A CLR null string is the absent DOM value, and the DOM says that is null rather than the
        // string "null" — JsValue.String documents this and 200-odd bridge sites depend on it.
        Assert.Equal(JsValueKind.Null, JsValue.String(null).Kind);
    }

    [Fact]
    public void MissingIsNotUndefinedAndIsWhatADefaultValueMeans()
    {
        // The distinction 598 argument reads in the bridge depend on: `scrollTo()` and
        // `scrollTo(undefined)` are different calls.
        Assert.True(default(JsValue).IsMissing);
        Assert.True(JsValue.Missing != JsValue.Undefined);
        Assert.False(JsValue.Missing.IsUndefined);
        Assert.False(JsValue.Undefined.IsMissing);
    }

    [Fact]
    public void IsNullishCoversMissingNullAndUndefined()
    {
        Assert.True(JsValue.Missing.IsNullish);
        Assert.True(JsValue.Null.IsNullish);
        Assert.True(JsValue.Undefined.IsNullish);

        Assert.False(JsValue.False.IsNullish);
        Assert.False(JsValue.Number(0d).IsNullish);
        Assert.False(JsValue.String(string.Empty).IsNullish);
    }

    [Fact]
    public void AsBooleanIsEcmaScriptTruthinessWithoutEnteringTheEngine()
    {
        Assert.False(JsValue.String(string.Empty).AsBoolean);

        // "0" is a non-empty string and therefore truthy, while the number 0 is not. A host that
        // reached for ToNumber here would get the opposite answer for the string.
        Assert.True(JsValue.String("0").AsBoolean);
        Assert.False(JsValue.Number(0d).AsBoolean);
        Assert.False(JsValue.Number(-0d).AsBoolean);
        Assert.False(JsValue.Number(double.NaN).AsBoolean);
        Assert.True(JsValue.Number(double.NegativeInfinity).AsBoolean);
        Assert.False(JsValue.Missing.AsBoolean);
        Assert.False(JsValue.Null.AsBoolean);
        Assert.False(JsValue.Undefined.AsBoolean);
    }

    [Fact]
    public void AsNumberDoesNotCoerceAStringAndAsStringDoesNotCoerceANumber()
    {
        // The cheap conversions answer only what the handle already knows; the coercions are on the
        // realm because they can run page script.
        Assert.True(double.IsNaN(JsValue.String("42").AsNumber));
        Assert.Equal(1d, JsValue.True.AsNumber);
        Assert.Equal(0d, JsValue.False.AsNumber);
        Assert.Null(JsValue.Number(42d).AsString);
        Assert.Equal("42", JsValue.String("42").AsString);
    }

    [Fact]
    public void StrictEqualityAndReflexiveEqualityDifferOnNaNAlone()
    {
        var nan = JsValue.Number(double.NaN);

        // `===` says a NaN is not itself, and JsValue.Equals says it is. Both are deliberate and the
        // suite asserts both, because a provider or a refactor that "fixed" either one would break
        // the other's caller: the language on one side, List.Contains on the other.
#pragma warning disable CS1718 // Comparing a value to itself IS the assertion here.
        Assert.False(nan == nan);
        Assert.True(nan != nan);
#pragma warning restore CS1718
        Assert.True(nan.Equals(nan));

        Assert.True(JsValue.String("a") == JsValue.String("a"));
        Assert.True(JsValue.Number(1d) == JsValue.Number(1d));
        Assert.False(JsValue.Number(1d) == JsValue.String("1"));
        Assert.True(JsValue.Missing == default);

        // Reflexive equality has to be usable as a dictionary key, so the hash has to agree with it.
        Assert.Equal(JsValue.String("a").GetHashCode(), JsValue.String("a").GetHashCode());
        Assert.Equal(nan.GetHashCode(), JsValue.Number(double.NaN).GetHashCode());
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void AnObjectIsTruthyAndTwoObjectHandlesCompareByIdentity(string engine)
    {
        using var realm = NewRealm(engine);

        var first = realm.NewObject();
        var second = realm.NewObject();

        Assert.Equal(JsValueKind.Object, first.Kind);
        Assert.True(first.IsObject);
        Assert.True(first.AsBoolean);

        // `{} === {}` is false and `x === x` is true, and a provider gets the second one right only
        // by handing back a handle over the engine's own object rather than a fresh wrapper — which
        // is what the seven wrapper tables in the bridge are keyed on.
        Assert.False(first == second);
#pragma warning disable CS1718 // Comparing a handle to itself IS the assertion here.
        Assert.True(first == first);
#pragma warning restore CS1718
        Assert.False(first.Equals(second));
        Assert.True(first.Equals(first));

        Assert.Equal(JsValueKind.Array, realm.NewArray().Kind);
        Assert.True(realm.NewArray().IsArray);
        Assert.Equal(JsValueKind.Function, realm.NewMethod("f", static (in JsCall _) => JsValue.Undefined).Kind);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void TheCoercionsEnterTheEngineWhereTheCheapConversionsCannot(string engine)
    {
        using var realm = NewRealm(engine);

        // The one correctness trap of the whole JSEAL migration: JsValue.ToString() is a diagnostic
        // rendering that never enters the engine, and realm.ToJsString is the observable ECMAScript
        // coercion, which on an object runs the toString the page wrote.
        var speaks = realm.EvaluateHostScript("({ toString: function () { return 'spoken'; } })", "test:tostring");

        Assert.Equal("[object]", speaks.ToString());
        Assert.Equal("spoken", realm.ToJsString(speaks));

        Assert.Equal(42d, realm.ToNumber(JsValue.String("42")));
        Assert.True(double.IsNaN(JsValue.String("42").AsNumber));
    }

    // ── members ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Engines))]
    public void DefineValueAndGetPropertyRoundTrip(string engine)
    {
        using var realm = NewRealm(engine);

        var target = realm.NewObject();
        realm.DefineValue(target, "answer", JsValue.Number(42d));
        realm.DefineValue(target, "label", JsValue.String("forty-two"));

        Assert.True(realm.GetProperty(target, "answer") == JsValue.Number(42d));
        Assert.True(realm.GetProperty(target, "label") == JsValue.String("forty-two"));
        Assert.True(realm.HasProperty(target, "answer"));

        // A property that was never installed reads as undefined, not as Missing: Missing means the
        // host supplied no value, and a read always supplies one.
        Assert.True(realm.GetProperty(target, "absent").IsUndefined);
        Assert.False(realm.HasProperty(target, "absent"));

        Assert.True(realm.DeleteProperty(target, "answer"));
        Assert.False(realm.HasProperty(target, "answer"));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void AnAccessorsGetterAndSetterBothRun(string engine)
    {
        using var realm = NewRealm(engine);

        var stored = "initial";
        JsValue Get(in JsCall call) => JsValue.String(stored);
        JsValue Set(in JsCall call)
        {
            stored = call.Realm.ToJsString(call[0]);
            return JsValue.Undefined;
        }

        var target = realm.NewObject();
        realm.DefineAccessor(target, "value", Get, Set);

        Assert.True(realm.GetProperty(target, "value") == JsValue.String("initial"));

        realm.SetProperty(target, "value", JsValue.String("from-host"));
        Assert.Equal("from-host", stored);
        Assert.True(realm.GetProperty(target, "value") == JsValue.String("from-host"));

        // And from script, which is the direction a page uses.
        realm.DefineValue(realm.Global, "accessorProbe", target);
        Assert.Equal("from-script", Eval(realm, "(accessorProbe.value = 'from-script', accessorProbe.value)"));
        Assert.Equal("from-script", stored);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void AnAccessorWithNoSetterIgnoresAnAssignmentRatherThanThrowing(string engine)
    {
        using var realm = NewRealm(engine);

        // 216 read-only IDL attributes are spelled as a null setter, so what an assignment to one
        // does is a contract outcome and not an implementation detail. Sloppy-mode assignment to an
        // accessor with no setter is a silent no-op in the language, and this is what the provider
        // does; a strict-mode assignment is a TypeError, which the second half pins.
        var target = realm.NewObject();
        realm.DefineAccessor(target, "readOnly", static (in JsCall _) => JsValue.String("fixed"), setter: null);
        realm.DefineValue(realm.Global, "readOnlyProbe", target);

        realm.SetProperty(target, "readOnly", JsValue.String("ignored"));
        Assert.True(realm.GetProperty(target, "readOnly") == JsValue.String("fixed"));

        Assert.Equal("fixed", Eval(realm, "(readOnlyProbe.readOnly = 'ignored', readOnlyProbe.readOnly)"));

        Assert.Equal(
            "TypeError",
            Eval(
                realm,
                """
                (function () {
                  'use strict';
                  try { readOnlyProbe.readOnly = 'ignored'; return 'no-throw'; }
                  catch (e) { return e.constructor.name; }
                })()
                """,
                "test:readonly-strict"));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void OwnPropertyNamesAnswersWhatWasInstalledInInstallationOrder(string engine)
    {
        using var realm = NewRealm(engine);

        var target = realm.NewObject();
        realm.DefineValue(target, "gamma", JsValue.Number(3d));
        realm.DefineValue(target, "alpha", JsValue.Number(1d));
        realm.DefineAccessor(target, "beta", static (in JsCall _) => JsValue.Number(2d), setter: null);

        // Creation order, not sorted order — the bridge's nested-browsing-context sweep diffs this
        // list across an evaluation and a reordering would make the diff report members that never
        // moved.
        Assert.Equal(new[] { "gamma", "alpha", "beta" }, realm.OwnPropertyNames(target));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void DefaultFlagsAreEnumerableAndNonEnumerableIsNot(string engine)
    {
        using var realm = NewRealm(engine);

        var target = realm.NewObject();
        realm.DefineValue(target, "visible", JsValue.Number(1d));
        realm.DefineValue(target, "hidden", JsValue.Number(2d), JsPropertyFlags.NonEnumerable);
        realm.DefineAccessor(target, "shown", static (in JsCall _) => JsValue.Number(3d), setter: null);
        realm.DefineAccessor(target, "unshown", static (in JsCall _) => JsValue.Number(4d), setter: null, JsPropertyFlags.NonEnumerable);
        realm.DefineValue(realm.Global, "flagsProbe", target);

        // Proved from JavaScript, because enumerability is a question the page asks and a host-side
        // answer would only be re-reading whatever the provider chose to record.
        Assert.Equal("visible,shown", Eval(realm, "Object.keys(flagsProbe).join(',')", "test:keys"));

        // The non-enumerable members are there; they just do not enumerate.
        Assert.Equal("2 4", Eval(realm, "flagsProbe.hidden + ' ' + flagsProbe.unshown", "test:hidden"));
        Assert.Equal(new[] { "visible", "shown" }, realm.OwnPropertyNames(target));

        // Configurable, which both flag sets carry: a page may delete or redefine a DOM member.
        Assert.Equal("true false", Eval(realm, "delete flagsProbe.hidden, (('visible' in flagsProbe) + ' ' + ('hidden' in flagsProbe))", "test:delete"));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void AnIndexedPropertyIsFoundByAnArrayGenericAndNotOnlyByAnIndexRead(string engine)
    {
        using var realm = NewRealm(engine);

        var target = realm.NewObject();
        realm.DefineIndex(target, 0, JsValue.String("zero"));
        realm.DefineIndex(target, 1, JsValue.String("one"));
        realm.DefineValue(target, "length", JsValue.Number(2d), JsPropertyFlags.NonEnumerable);
        realm.DefineValue(realm.Global, "indexProbe", target);

        Assert.True(realm.GetIndex(target, 1) == JsValue.String("one"));

        // An array generic asks whether index i is PRESENT before reading it, which an index
        // installed under the string key "0" would answer no to — the defect that made a live
        // collection produce a hole per element under Array.prototype.map.call.
        Assert.Equal("zero|one", Eval(realm, "Array.prototype.join.call(indexProbe, '|')", "test:generic"));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void SetPrototypeLinksAnObjectToAnInterface(string engine)
    {
        using var realm = NewRealm(engine);

        var prototype = realm.NewObject();
        realm.DefineValue(prototype, "inherited", JsValue.String("from-prototype"));

        var instance = realm.NewObject();
        realm.SetPrototype(instance, prototype);

        // GetProperty follows the chain, which is what makes a wrapper's interface members reachable.
        Assert.True(realm.GetProperty(instance, "inherited") == JsValue.String("from-prototype"));
        Assert.True(realm.GetPrototype(instance) == prototype);

        // …and the chain is not the object's own names.
        Assert.DoesNotContain("inherited", realm.OwnPropertyNames(instance));

        realm.DefineValue(realm.Global, "protoProbe", instance);
        Assert.Equal("true", Eval(realm, "String(Object.getPrototypeOf(protoProbe) === Object.getPrototypeOf(protoProbe))", "test:proto"));
    }

    // ── calls ──────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Engines))]
    public void AHostFunctionSeesItsArgumentsItsReceiverAndMissingPastTheEnd(string engine)
    {
        using var realm = NewRealm(engine);

        var seen = "not-called";
        JsValue Record(in JsCall call)
        {
            seen = string.Join(
                '|',
                call.Length.ToString(),
                call[0].AsString ?? "?",
                call[1].IsMissing ? "missing" : call[1].Kind.ToString(),
                call.Realm.ToJsString(call.Realm.GetProperty(call.This, "tag")),
                call.NewTarget.IsMissing ? "no-new-target" : "new-target");

            return JsValue.String("returned");
        }

        realm.DefineValue(realm.Global, "record", realm.NewMethod("record", Record, 2));

        Assert.Equal("returned", Eval(realm, "record.call({ tag: 'receiver' }, 'first')", "test:args"));
        Assert.Equal("1|first|missing|receiver|no-new-target", seen);

        // An argument explicitly passed as undefined is NOT missing, which is the distinction the
        // bridge's arity-sensitive operations turn on.
        Assert.Equal("returned", Eval(realm, "record.call({ tag: 'receiver' }, 'first', undefined)", "test:args-undefined"));
        Assert.Equal("2|first|Undefined|receiver|no-new-target", seen);

        // The declared name and length reach the page, because WebIDL says an operation has both.
        Assert.Equal("record 2", Eval(realm, "record.name + ' ' + record.length", "test:name-length"));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void InvokeAndConstructRunAScriptFunctionFromTheHost(string engine)
    {
        using var realm = NewRealm(engine);

        var add = realm.EvaluateHostScript("(function (a, b) { return a + b; })", "test:add");
        Assert.True(add.IsFunction);
        Assert.True(realm.Invoke(add, JsValue.Undefined, [JsValue.Number(20d), JsValue.Number(22d)]) == JsValue.Number(42d));

        var receiver = realm.NewObject();
        realm.DefineValue(receiver, "tag", JsValue.String("mine"));
        var readTag = realm.EvaluateHostScript("(function () { return this.tag; })", "test:this");
        Assert.True(realm.Invoke(readTag, receiver) == JsValue.String("mine"));

        var point = realm.EvaluateHostScript("(function Point(x) { this.x = x; })", "test:ctor");
        var instance = realm.Construct(point, [JsValue.Number(7d)]);
        Assert.True(instance.IsObject);
        Assert.True(realm.GetProperty(instance, "x") == JsValue.Number(7d));
        Assert.True(realm.GetPrototype(instance) == realm.GetProperty(point, "prototype"));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void AHostFunctionThatThrowsIsCatchableFromScriptAsTheErrorRealmErrorNamed(string engine)
    {
        using var realm = NewRealm(engine);

        realm.DefineValue(
            realm.Global,
            "boom",
            realm.NewMethod("boom", static (in JsCall call) => throw call.Realm.Error(JsErrorKind.TypeError, "no good")));

        Assert.Equal(
            "TypeError|no good|true",
            Eval(
                realm,
                """
                (function () {
                  try { boom(); return 'no-throw'; }
                  catch (e) { return e.constructor.name + '|' + e.message + '|' + (e instanceof TypeError); }
                })()
                """,
                "test:throw"));

        // Every kind reaches its own constructor, so a host that raises a RangeError does not get a
        // page branching on `instanceof TypeError` by accident.
        realm.DefineValue(
            realm.Global,
            "outOfRange",
            realm.NewMethod("outOfRange", static (in JsCall call) => throw call.Realm.Error(JsErrorKind.RangeError, "too big")));

        Assert.Equal(
            "RangeError|too big",
            Eval(
                realm,
                "(function () { try { outOfRange(); return 'no-throw'; } catch (e) { return e.constructor.name + '|' + e.message; } })()",
                "test:throw-range"));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void AScriptThrowReachesAHostCallerAsAJsEngineExceptionCarryingTheThrownValue(string engine)
    {
        using var realm = NewRealm(engine);

        var thrower = realm.EvaluateHostScript("(function () { throw { code: 5 }; })", "test:thrower");

        // The value the page threw travels, not just its message: `throw 42` and `throw {code: 5}`
        // are both legal and a host that reported only a message would lose what the page said.
        var failure = Assert.Throws<JsEngineException>(() => realm.Invoke(thrower, JsValue.Undefined));
        Assert.True(failure.Thrown.IsObject);
        Assert.True(realm.GetProperty(failure.Thrown, "code") == JsValue.Number(5d));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void DomErrorIsConstructedThroughTheRealmsDomExceptionSoItsNameReachesThePage(string engine)
    {
        using var realm = NewRealm(engine);

        // A bare realm has no DOM globals — building them is the bridge's job, not the provider's —
        // so the constructor the contract names is installed here first. That is also the assertion:
        // DomError must go through the realm's own DOMException rather than mint an error of its
        // own, because a page branches on `name` and on `instanceof DOMException`.
        realm.EvaluateHostScript(
            "function DOMException(message, name) { this.message = message; this.name = name; }",
            "test:domexception");

        realm.DefineValue(
            realm.Global,
            "notFound",
            realm.NewMethod("notFound", static (in JsCall call) => throw call.Realm.DomError("NotFoundError", "no such node")));

        Assert.Equal(
            "NotFoundError|no such node|true",
            Eval(
                realm,
                """
                (function () {
                  try { notFound(); return 'no-throw'; }
                  catch (e) { return e.name + '|' + e.message + '|' + (e instanceof DOMException); }
                })()
                """,
                "test:domerror"));
    }

    // ── method versus constructor ──────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Engines))]
    public void NewMethodIsNotConstructableAndHasNoPrototype(string engine)
    {
        using var realm = NewRealm(engine);

        realm.DefineValue(realm.Global, "operation", realm.NewMethod("operation", static (in JsCall _) => JsValue.String("called")));

        // WebIDL: only an interface object is a constructor. `el.setAttribute.prototype` is
        // undefined in a browser and `new el.setAttribute()` is a TypeError — and under this engine
        // the two are the same fact, because IsConstructor tests for the prototype object. It is
        // also the allocation DomFunction was introduced to stop: a prototype object plus its
        // constructor back-reference per member, on wrappers with ~149 members each.
        Assert.Equal("undefined", Eval(realm, "typeof operation.prototype", "test:method-prototype"));
        Assert.True(realm.GetProperty(realm.GetProperty(realm.Global, "operation"), "prototype").IsUndefined);

        Assert.Equal(
            "TypeError",
            Eval(
                realm,
                "(function () { try { new operation(); return 'constructed'; } catch (e) { return e.constructor.name; } })()",
                "test:method-new"));

        // It is still perfectly callable, which is the only thing an operation has to be.
        Assert.Equal("called", Eval(realm, "operation()", "test:method-call"));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void NewConstructorIsConstructableAndCarriesTheInterfacePrototype(string engine)
    {
        using var realm = NewRealm(engine);

        var made = 0;
        JsValue Body(in JsCall call)
        {
            made++;
            call.Realm.SetProperty(call.This, "size", call[0].IsMissing ? JsValue.Number(0d) : call[0]);
            return JsValue.Undefined;
        }

        var constructor = realm.NewConstructor("Widget", Body, 1);
        var prototype = realm.GetProperty(constructor, "prototype");
        Assert.True(prototype.IsObject);

        // An interface's members are installed on that prototype, and an instance inherits them.
        realm.DefineValue(prototype, "kind", JsValue.String("widget"));
        realm.DefineValue(realm.Global, "Widget", constructor);

        var instance = realm.Construct(constructor, [JsValue.Number(3d)]);
        Assert.Equal(1, made);
        Assert.True(realm.GetProperty(instance, "size") == JsValue.Number(3d));
        Assert.True(realm.GetProperty(instance, "kind") == JsValue.String("widget"));

        Assert.Equal("widget|4|true", Eval(realm, "(function () { var w = new Widget(4); return w.kind + '|' + w.size + '|' + (w instanceof Widget); })()", "test:construct"));
        Assert.Equal(2, made);

        // Construct performs a real [[Construct]] and not a call with a fresh receiver, which is
        // visible to a scripted constructor as its own new.target.
        var scripted = realm.EvaluateHostScript(
            "(function Scripted() { this.seen = String(new.target && new.target.name); })",
            "test:new-target-script");

        Assert.True(realm.GetProperty(realm.Construct(scripted), "seen") == JsValue.String("Scripted"));
    }

    /// <summary>
    /// <c>new.target</c> inside a host constructor's own body.
    /// </summary>
    /// <remarks>
    /// <b>Skipped because the Broiler.JS provider does not answer it, which is a defect in the
    /// provider rather than in this test.</b> <c>JsCall.NewTarget</c> promises the construct target
    /// for a construct call, and <c>BroilerJsRealm.Dispatch</c> reads it from
    /// <c>JSEngine.NewTarget</c> — which resolves <c>Frames.CurrentNewTarget</c>, the interpreter's
    /// frame stack. A native function's body is invoked as a delegate and pushes no such frame, so
    /// the read is null and every host constructor sees <see cref="JsValue.Missing"/>. The value is
    /// there to be had: the engine's own [[Construct]] sets <c>ec.CurrentNewTarget</c> to the
    /// constructor immediately before invoking the delegate, and its <c>Object</c> factory reads
    /// exactly that. Custom-element construction is the caller that will need it, and it is smuggling
    /// new.target through as argument zero from a JavaScript shim today for want of this.
    /// Un-skipping this test is what "fixed" means.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Engines))]
    public void NewTargetIsTheConstructorInsideAHostConstructBody(string engine)
    {
        using var realm = NewRealm(engine);

        var target = "unset";
        realm.DefineValue(
            realm.Global,
            "Probe",
            realm.NewConstructor("Probe", (in JsCall call) =>
            {
                target = call.NewTarget.IsMissing
                    ? "missing"
                    : call.Realm.ToJsString(call.Realm.GetProperty(call.NewTarget, "name"));

                return JsValue.Undefined;
            }));

        realm.EvaluateHostScript("new Probe()", "test:new-target");
        Assert.Equal("Probe", target);
    }

    // ── jobs ───────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Engines))]
    public void DrainJobsRunsWhatWasQueuedAndReportsHowMany(string engine)
    {
        using var realm = NewRealm(engine);

        var order = new List<string>();
        Assert.False(realm.HasPendingJobs);

        realm.EnqueueJob(() => order.Add("first"));
        realm.EnqueueJob(() => order.Add("second"));
        Assert.True(realm.HasPendingJobs);

        // Nothing has run yet: the host decides when a microtask checkpoint happens, which is the
        // whole reason this contract is pull-shaped.
        Assert.Empty(order);

        Assert.Equal(2, realm.DrainJobs());
        Assert.Equal(new[] { "first", "second" }, order);
        Assert.False(realm.HasPendingJobs);
        Assert.Equal(0, realm.DrainJobs());
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void AJobQueuedByAJobIsFollowedWithinTheSameDrain(string engine)
    {
        using var realm = NewRealm(engine);

        var order = new List<string>();
        realm.EnqueueJob(() =>
        {
            order.Add("outer");
            realm.EnqueueJob(() => order.Add("inner"));
        });

        Assert.Equal(2, realm.DrainJobs());
        Assert.Equal(new[] { "outer", "inner" }, order);

        // …and the limit is what keeps a chain that re-queues itself from holding the checkpoint
        // forever. It is a bound on jobs run, not a bound on depth.
        var ran = 0;
        void Requeue()
        {
            ran++;
            realm.EnqueueJob(Requeue);
        }

        realm.EnqueueJob(Requeue);
        Assert.Equal(3, realm.DrainJobs(3));
        Assert.Equal(3, ran);
        Assert.True(realm.HasPendingJobs);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void APromiseSettlesFromTheHostAndItsReactionRunsAtTheNextDrain(string engine)
    {
        using var realm = NewRealm(engine);

        if (Lacks(realm, JsCapabilities.Promises))
        {
            // An engine that cannot hand a pending promise to the host has no deferred result for
            // fetch or whenDefined to return, and must say so rather than hand back something that
            // never settles.
            Assert.Throws<JsCapabilityUnavailableException>(() => realm.NewPromise(out _, out _));
            return;
        }

        var promise = realm.NewPromise(out var resolve, out _);
        realm.DefineValue(realm.Global, "pending", promise);
        realm.EvaluateHostScript("var seen = 'none'; pending.then(function (v) { seen = 'got:' + v; });", "test:then");

        Assert.Equal("none", Eval(realm, "seen", "test:then-before"));

        resolve(JsValue.String("value"));

        // A reaction is a job, not a callback: it must not have run at the moment the promise was
        // settled, and it must run at the next checkpoint the host takes.
        Assert.Equal("none", Eval(realm, "seen", "test:then-settled"));
        Assert.True(realm.DrainJobs() > 0);
        Assert.Equal("got:value", Eval(realm, "seen", "test:then-after"));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void ARejectedPromiseReachesItsRejectionHandler(string engine)
    {
        using var realm = NewRealm(engine);

        if (Lacks(realm, JsCapabilities.Promises))
        {
            Assert.Throws<JsCapabilityUnavailableException>(() => realm.NewPromise(out _, out _));
            return;
        }

        var promise = realm.NewPromise(out _, out var reject);
        realm.DefineValue(realm.Global, "failing", promise);
        realm.EvaluateHostScript("var failure = 'none'; failing.then(null, function (e) { failure = 'caught:' + e; });", "test:catch");

        reject(JsValue.String("nope"));
        realm.DrainJobs();

        Assert.Equal("caught:nope", Eval(realm, "failure", "test:catch-after"));
    }

    // ── source ─────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Engines))]
    public void EvaluatingHostScriptAnswersTheValueOfTheLastExpression(string engine)
    {
        using var realm = NewRealm(engine);

        Assert.True(realm.EvaluateHostScript("2 + 3", "test:host") == JsValue.Number(5d));
        Assert.True(realm.EvaluateHostScript("'a' + 'b'", "test:host-string") == JsValue.String("ab"));
        Assert.True(realm.EvaluateHostScript("({ a: 1 })", "test:host-object").IsObject);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void ARealmBuiltWithoutGuestEvalRefusesGuestSourceAndStillRunsHostScript(string engine)
    {
        var provider = Provider(engine);

        using (var permissive = provider.CreateRealm(JsRealmOptions.Default))
        {
            // The default realm runs both, and the split is only visible when a host asks for it.
            Assert.True(permissive.Capabilities.HasFlag(JsCapabilities.GuestEval));
            Assert.True(permissive.EvaluateGuestSource("6 * 7", "test:guest") == JsValue.Number(42d));
        }

        using var restricted = provider.CreateRealm(new JsRealmOptions { AllowGuestEval = false });

        // A realm is never wider than its provider and may be narrower. This is the only narrowing
        // the options can express, and it is the whole point of IJsSource: the bridge's own
        // JavaScript is not subject to the page's Content-Security-Policy and the page's eval is.
        Assert.False(restricted.Capabilities.HasFlag(JsCapabilities.GuestEval));
        Assert.Equal(
            provider.Capabilities & ~JsCapabilities.GuestEval,
            restricted.Capabilities & ~JsCapabilities.GuestEval);

        var refusal = Assert.Throws<JsCapabilityUnavailableException>(
            () => restricted.EvaluateGuestSource("6 * 7", "test:guest-refused"));
        Assert.Equal(JsCapabilities.GuestEval, refusal.Missing);
        Assert.Equal(restricted.EngineName, refusal.EngineName);

        Assert.True(restricted.EvaluateHostScript("6 * 7", "test:host-still-runs") == JsValue.Number(42d));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void ASyntaxErrorInSourceReachesTheHostAsAnEngineException(string engine)
    {
        using var realm = NewRealm(engine);

        // Not a capability failure: the host asked for something the realm can do, and the source
        // was wrong. A host that cannot tell those apart cannot report either usefully.
        Assert.Throws<JsEngineException>(() => realm.EvaluateHostScript("function (", "test:syntax"));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void ATopLevelDeclarationBecomesAPropertyOfTheGlobal(string engine)
    {
        using var realm = NewRealm(engine);

        if (Lacks(realm, JsCapabilities.GlobalIsVariableScope))
        {
            // The bridge depends on this and does not know it does: a nested browsing context
            // recovers a frame's declarations by diffing the global's own names across the
            // evaluation, which finds nothing at all on an engine that scopes them elsewhere.
            realm.EvaluateHostScript("var declaredAtTopLevel = 7;", "test:var");
            Assert.True(realm.GetProperty(realm.Global, "declaredAtTopLevel").IsUndefined);
            return;
        }

        var before = realm.OwnPropertyNames(realm.Global);
        realm.EvaluateHostScript("var declaredAtTopLevel = 7; function declaredFunction() { return 8; }", "test:var");
        var after = realm.OwnPropertyNames(realm.Global);

        Assert.True(realm.GetProperty(realm.Global, "declaredAtTopLevel") == JsValue.Number(7d));

        // Both land, and in declaration-instantiation order rather than source order:
        // GlobalDeclarationInstantiation creates the function bindings before the var ones. The
        // sweep this contract exists for reads the diff, so the order it sees is worth pinning.
        Assert.Equal(new[] { "declaredFunction", "declaredAtTopLevel" }, after.Except(before).ToArray());
        Assert.True(realm.Global.IsObject);
    }

    // ── exotic objects ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Engines))]
    public void AnOrdinaryPropertyWinsOverTheExoticHandler(string engine)
    {
        using var realm = NewRealm(engine);

        var handler = new RecordingExotic();

        if (Lacks(realm, JsCapabilities.ExoticObjects))
        {
            Assert.Throws<JsCapabilityUnavailableException>(() => realm.NewExotic(handler));
            return;
        }

        var collection = realm.NewExotic(handler);

        // The case IJsExotic.cs describes in as many words: a collection containing an element
        // NAMED "item" must not shadow its own item() method. Ordinary properties are consulted
        // first and the handler answers only what they did not — getting this backwards is silently
        // wrong, and the failure is a page whose search box stops working, months later.
        realm.DefineValue(collection, "item", realm.NewMethod("item", static (in JsCall _) => JsValue.String("the method")));
        realm.DefineValue(realm.Global, "collection", collection);

        Assert.True(realm.GetProperty(collection, "item").IsFunction);
        Assert.Equal("the method", Eval(realm, "collection.item()", "test:exotic-item"));
        Assert.DoesNotContain("item", handler.AskedNames);

        // …and a name the ordinary properties do NOT have reaches the handler, from the host and
        // from script alike.
        Assert.True(realm.GetProperty(collection, "named") == JsValue.String("named:named"));
        Assert.Equal("named:named", Eval(realm, "collection.named", "test:exotic-named"));
        Assert.Contains("named", handler.AskedNames);

        // A name neither side has is undefined rather than an invention of the handler's.
        Assert.True(realm.GetProperty(collection, "nothing").IsUndefined);

        // Indexed lookup is asked of the handler at the moment of the read, so a live collection
        // reports what it holds now.
        Assert.True(realm.GetIndex(collection, 1) == JsValue.String("element 1"));
        Assert.Equal("element 0|element 1", Eval(realm, "collection[0] + '|' + collection[1]", "test:exotic-index"));

        handler.Count = 3;
        Assert.Equal("element 2", Eval(realm, "String(collection[2])", "test:exotic-grown"));

        // Shrinking matters as much as growing: an index whose element went away has to stop being
        // offered rather than keep answering with a stale handle, and it has to stop being a name
        // the object enumerates.
        handler.Count = 1;
        Assert.Equal(
            "undefined|undefined|0,item,named",
            Eval(
                realm,
                "String(collection[1]) + '|' + String(collection[2]) + '|' + Object.getOwnPropertyNames(collection).join(',')",
                "test:exotic-shrunk"));

        // A named write is offered to the handler before it becomes an ordinary property, because a
        // legacy platform object with a named setter has to see the value first.
        realm.EvaluateHostScript("collection.stored = 'written';", "test:exotic-set");
        Assert.Equal("stored=written", Assert.Single(handler.Writes));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void AnExoticObjectEnumeratesItsIndicesAndItsSupportedNames(string engine)
    {
        using var realm = NewRealm(engine);

        var handler = new RecordingExotic();

        if (Lacks(realm, JsCapabilities.ExoticObjects))
        {
            Assert.Throws<JsCapabilityUnavailableException>(() => realm.NewExotic(handler));
            return;
        }

        var collection = realm.NewExotic(handler);
        realm.DefineValue(collection, "item", JsValue.String("the method"));
        realm.DefineValue(realm.Global, "enumerable", collection);

        // Indices, then ordinary properties, then the handler's supported names — the order the
        // provider's enumerator produces, pinned so that a second provider has a shape to match
        // rather than a blank to fill in. The indices are in the list at all because presence,
        // enumeration and retrieval are separate entry points with no single hook between them, so
        // an index has to be a real own property for any generic algorithm to find it.
        Assert.Equal(
            "0,1,item,named",
            Eval(
                realm,
                "(function () { var seen = []; for (var k in enumerable) { seen.push(k); } return seen.join(','); })()",
                "test:exotic-forin"));

        Assert.Equal("0,1,item,named", Eval(realm, "Object.getOwnPropertyNames(enumerable).join(',')", "test:exotic-own"));

        // `in` reaches the handler too, which is what makes a named lookup answerable before it is
        // read rather than only when it is.
        Assert.Equal("true", Eval(realm, "String('named' in enumerable)", "test:exotic-in"));
        Assert.Equal("false", Eval(realm, "String('nothing' in enumerable)", "test:exotic-not-in"));
    }

    /// <summary>
    /// An exotic object's supported names in <c>Object.keys</c> and in a spread.
    /// </summary>
    /// <remarks>
    /// <b>Skipped because the Broiler.JS provider answers neither, which is a defect in the provider
    /// rather than in this test.</b> <see cref="IJsExotic.SupportedNames"/> says in as many words
    /// that the names are "for <c>Object.keys</c>, <c>for…in</c> and spread", and the provider
    /// supplies them by appending to <c>GetAllKeys</c> — which is enough for <c>for…in</c> and
    /// <c>Object.getOwnPropertyNames</c> (both pass, above) and not enough for the other two.
    /// <c>Object.keys</c> implements EnumerableOwnProperties: it snapshots the own keys and then asks
    /// <c>[[GetOwnProperty]]</c> for each one, keeping only the enumerable ones — and a supported
    /// name has no own descriptor, so it is dropped. <c>Object.assign</c>, and therefore an object
    /// spread, filters the same way. A page enumerating a form's controls with
    /// <c>Object.keys(form.elements)</c> or <c>{...form.elements}</c> gets the indices and the
    /// interface's own members but none of the named controls.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Engines))]
    public void AnExoticObjectsSupportedNamesReachObjectKeysAndSpread(string engine)
    {
        using var realm = NewRealm(engine);

        var handler = new RecordingExotic();

        if (Lacks(realm, JsCapabilities.ExoticObjects))
        {
            Assert.Throws<JsCapabilityUnavailableException>(() => realm.NewExotic(handler));
            return;
        }

        var collection = realm.NewExotic(handler);
        realm.DefineValue(collection, "item", JsValue.String("the method"));
        realm.DefineValue(realm.Global, "enumerable", collection);

        Assert.Equal("0,1,item,named", Eval(realm, "Object.keys(enumerable).join(',')", "test:exotic-keys"));
        Assert.Equal("0,1,item,named", Eval(realm, "Object.keys(Object.assign({}, enumerable)).join(',')", "test:exotic-spread"));
    }

    /// <summary>
    /// An <see cref="IJsExotic"/> that records what it was asked, so the ordering rule can be
    /// asserted from the handler's side as well as from the value's.
    /// </summary>
    private sealed class RecordingExotic : IJsExotic
    {
        private static readonly string[] Names = ["named"];

        /// <summary>Every name the handler was consulted about, answered or not.</summary>
        /// <remarks>
        /// Asked, not answered, because the ordering rule is about whether the handler is
        /// <em>reached</em>: a name the ordinary properties satisfied must never arrive here at all.
        /// </remarks>
        internal List<string> AskedNames { get; } = [];

        internal List<string> Writes { get; } = [];

        internal uint Count { get; set; } = 2;

        public bool TryGetNamed(string name, out JsValue value)
        {
            AskedNames.Add(name);

            if (!Names.Contains(name))
            {
                value = JsValue.Missing;
                return false;
            }

            value = JsValue.String($"named:{name}");
            return true;
        }

        public bool TryGetIndex(uint index, out JsValue value)
        {
            if (index >= Count)
            {
                value = JsValue.Missing;
                return false;
            }

            value = JsValue.String($"element {index}");
            return true;
        }

        public bool TrySetNamed(string name, JsValue value)
        {
            Writes.Add($"{name}={value.AsString}");
            return true;
        }

        public IReadOnlyList<string> SupportedNames => Names;

        public uint IndexedLength => Count;
    }

    // ── re-entrancy and worker realms ──────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Engines))]
    public void AHostFunctionMayCallBackIntoScriptWhileTheEngineIsInsideIt(string engine)
    {
        using var realm = NewRealm(engine);

        JsValue Twice(in JsCall call)
        {
            var callback = call[0];
            if (!callback.IsFunction)
                throw call.Realm.Error(JsErrorKind.TypeError, "a function is required");

            // The engine is inside this host call right now. An event listener, a promise reaction
            // and a toString coercion are all exactly this, so an engine without it can run a page's
            // script but cannot dispatch a click.
            var first = call.Realm.Invoke(callback, JsValue.Undefined, [JsValue.Number(20d)]);
            var second = call.Realm.Invoke(callback, JsValue.Undefined, [JsValue.Number(22d)]);
            return JsValue.Number(first.AsNumber + second.AsNumber);
        }

        if (Lacks(realm, JsCapabilities.ReentrantHostCalls))
        {
            realm.DefineValue(realm.Global, "twice", realm.NewMethod("twice", Twice));
            Assert.Throws<JsEngineException>(() => realm.EvaluateHostScript("twice(function (n) { return n; })", "test:reentrant"));
            return;
        }

        realm.DefineValue(realm.Global, "twice", realm.NewMethod("twice", Twice, 1));
        Assert.Equal("42", Eval(realm, "String(twice(function (n) { return n; }))", "test:reentrant"));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void ASecondRealmRunsOnASecondThread(string engine)
    {
        var provider = Provider(engine);
        using var first = provider.CreateRealm(JsRealmOptions.Default);

        if (Lacks(first, JsCapabilities.WorkerRealms))
            return;

        // Nothing promises that two threads may touch ONE realm — the contract says so and this
        // provider does not permit it — so the second realm is built and used entirely on the
        // second thread, which is what a Worker does.
        //
        // The capability names two things ("a second realm can be created on another thread AND
        // values moved between the two by structured clone"), so this exercises both: a message goes
        // out as a JsDetachedValue, is adopted on the worker thread, and a reply comes back the same
        // way. A test that only built the realm would leave half the claim unchecked.
        var outbound = first.NewObject();
        first.DefineValue(outbound, "greeting", JsValue.String("from the page"));
        var sent = first.Detach(outbound);

        JsValue secondGlobal = default;
        var answer = string.Empty;
        var received = string.Empty;
        JsDetachedValue? reply = null;
        Exception? failure = null;

        var worker = new Thread(() =>
        {
            try
            {
                using var second = provider.CreateRealm(JsRealmOptions.Default);
                second.EvaluateHostScript("var inWorker = 'worker';", "test:worker");
                answer = second.ToJsString(second.GetProperty(second.Global, "inWorker"));
                secondGlobal = second.Global;

                // Adopted HERE, on this thread, by THIS realm — which is what makes the resulting
                // objects the worker's own rather than the page's.
                var inbound = second.Adopt(sent);
                received = second.ToJsString(second.GetProperty(inbound, "greeting"));

                var outgoing = second.NewObject();
                second.DefineValue(outgoing, "greeting", JsValue.String("from the worker"));
                reply = second.Detach(outgoing);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(30)), "the worker realm did not finish");
        Assert.Null(failure);
        Assert.Equal("worker", answer);
        Assert.Equal("from the page", received);

        // Two realms, not one shared one: a declaration in the worker is not visible here.
        Assert.False(first.Global == secondGlobal);
        Assert.True(first.GetProperty(first.Global, "inWorker").IsUndefined);

        // And the reply crosses back the same way, into the realm that asks for it.
        Assert.NotNull(reply);
        var materialized = first.Adopt(reply!);
        Assert.Equal("from the worker", first.ToJsString(first.GetProperty(materialized, "greeting")));
    }

    // ── structured clone ───────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Engines))]
    public void ACloneIsACopyRatherThanTheSameObject(string engine)
    {
        using var realm = NewRealm(engine);

        if (Lacks(realm, JsCapabilities.WorkerRealms))
            return;

        var original = realm.NewObject();
        realm.DefineValue(original, "n", JsValue.Number(1d));

        var copy = realm.Clone(original);

        // Not the same object, and not sharing state with it: mutating the source afterwards is
        // invisible to the copy, which is the property the messaging model depends on.
        Assert.False(copy == original);
        Assert.Equal(1d, realm.GetProperty(copy, "n").AsNumber);
        realm.SetProperty(original, "n", JsValue.Number(2d));
        Assert.Equal(1d, realm.GetProperty(copy, "n").AsNumber);

        // A primitive clones to itself, which is why a carrier cannot be a JsValue handle: there is
        // no engine instance in this answer to key one on.
        Assert.Equal("text", realm.Clone(JsValue.String("text")).AsString);
        Assert.True(realm.Clone(JsValue.Undefined).IsUndefined);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void CloningRefusesAValueTheAlgorithmDoesNotCover(string engine)
    {
        using var realm = NewRealm(engine);

        if (Lacks(realm, JsCapabilities.WorkerRealms))
            return;

        // A function is the canonical uncloneable value, and a page reaches this by writing
        // postMessage(function () {}). The failure has to be an exception the host can catch and
        // turn into a DataCloneError, not a silently empty object.
        var uncloneable = realm.NewMethod("f", static (in _) => JsValue.Undefined);
        Assert.Throws<JsEngineException>(() => realm.Clone(uncloneable));
        Assert.Throws<JsEngineException>(() => realm.Detach(uncloneable));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void ATransferListDetachesItsSourceAndCarriesTheContents(string engine)
    {
        using var realm = NewRealm(engine);

        if (Lacks(realm, JsCapabilities.WorkerRealms) || Lacks(realm, JsCapabilities.HostScriptSource))
            return;

        var buffer = realm.EvaluateHostScript(
            "(function () { var b = new ArrayBuffer(4); new Uint8Array(b)[0] = 7; return b; })()",
            "test:transfer");

        Assert.Equal(JsTransferKind.Transferable, realm.ClassifyTransferable(buffer));

        var payload = realm.NewObject();
        realm.DefineValue(payload, "buffer", buffer);
        var moved = realm.Clone(payload, [buffer]);

        // The observable half of "transfer": the source is detached afterwards and the receiver has
        // the bytes. (What is NOT promised is zero copies; this engine copies and then detaches.)
        Assert.Equal(JsTransferKind.Detached, realm.ClassifyTransferable(buffer));
        realm.DefineValue(realm.Global, "moved", moved);
        Assert.Equal("7", Eval(realm, "String(new Uint8Array(moved.buffer)[0])", "test:transfer-read"));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void OnlyATransferableObjectClassifiesAsOne(string engine)
    {
        using var realm = NewRealm(engine);

        if (Lacks(realm, JsCapabilities.WorkerRealms))
            return;

        // The question a postMessage transfer list asks of every entry it does not recognise itself.
        // Everything that is not transferable answers the same way, including the hole a sparse
        // array hands over and the primitive a page puts in the list by mistake.
        Assert.Equal(JsTransferKind.NotTransferable, realm.ClassifyTransferable(realm.NewObject()));
        Assert.Equal(JsTransferKind.NotTransferable, realm.ClassifyTransferable(realm.NewArray()));
        Assert.Equal(JsTransferKind.NotTransferable, realm.ClassifyTransferable(JsValue.Number(1d)));
        Assert.Equal(JsTransferKind.NotTransferable, realm.ClassifyTransferable(JsValue.Missing));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void ADetachedCarrierBelongsToItsEngineAndToNoRealm(string engine)
    {
        var provider = Provider(engine);
        using var realm = provider.CreateRealm(JsRealmOptions.Default);

        if (Lacks(realm, JsCapabilities.WorkerRealms))
            return;

        var source = realm.NewObject();
        realm.DefineValue(source, "n", JsValue.Number(3d));
        var carrier = realm.Detach(source);

        Assert.Equal(realm.EngineName, carrier.EngineName);

        // Adopting twice yields two independent copies. One message delivered to two realms needs
        // that, and it is also what says the carrier is not itself a realm's object.
        var first = realm.Adopt(carrier);
        var second = realm.Adopt(carrier);
        Assert.False(first == second);
        Assert.Equal(3d, realm.GetProperty(first, "n").AsNumber);
        Assert.Equal(3d, realm.GetProperty(second, "n").AsNumber);

        // A carrier another engine minted is refused rather than walked. In a process with one
        // provider registered this is the only way to build one, and the refusal is what keeps two
        // linked engines from silently handing each other graphs neither can read.
        var foreign = Broiler.HtmlBridge.Jseal.Providers.JsProviderClone.Detached("not-this-engine", null);
        Assert.Throws<JsEngineException>(() => realm.Adopt(foreign));
    }

    // ── the two array questions the messaging transfer-list walk is built on ────────────────────

    [Theory]
    [MemberData(nameof(Engines))]
    public void OwnPropertyNamesOfAnArrayAreItsPresentIndicesAndNotItsLength(string engine)
    {
        using var realm = NewRealm(engine);

        if (Lacks(realm, JsCapabilities.HostScriptSource))
            return;

        // This is how the bridge walks a postMessage transfer list, and both halves matter. A HOLE
        // must be skipped: handing it on as a value would turn postMessage(m, [ , buf]) into a
        // DataCloneError a browser does not raise. And `length` must not appear, or the walk would
        // classify a number as a transfer-list entry.
        var sparse = realm.EvaluateHostScript("(function () { var a = ['x']; a[2] = 'z'; return a; })()", "test:sparse");

        Assert.Equal(new[] { "0", "2" }, realm.OwnPropertyNames(sparse));
        Assert.Equal("x", realm.GetIndex(sparse, 0).AsString);
        Assert.Equal("z", realm.GetIndex(sparse, 2).AsString);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void DefiningTheNextIndexOnAnArrayExtendsIt(string engine)
    {
        using var realm = NewRealm(engine);

        if (Lacks(realm, JsCapabilities.HostScriptSource))
            return;

        // Appending, as the worker global's listener array does it. An engine on which DefineIndex
        // left `length` behind would grow an array that JavaScript could not iterate, and the
        // listeners a worker registered would silently never be called.
        var array = realm.NewArray([JsValue.String("a")]);
        realm.DefineIndex(array, (uint)realm.GetProperty(array, "length").AsNumber, JsValue.String("b"));

        Assert.Equal(2d, realm.GetProperty(array, "length").AsNumber);
        Assert.Equal("b", realm.GetIndex(array, 1).AsString);

        realm.DefineValue(realm.Global, "appended", array);
        Assert.Equal("a,b", Eval(realm, "Array.prototype.join.call(appended, ',')", "test:append"));
    }

    // ── the realm's own answers about itself ───────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Engines))]
    public void ARealmNamesItsEngineAndIsNeverWiderThanIt(string engine)
    {
        var provider = Provider(engine);
        using var realm = provider.CreateRealm(JsRealmOptions.Default);

        Assert.Equal(provider.Name, realm.EngineName);
        Assert.Equal(engine, realm.EngineName);

        // "A realm's capabilities may be narrower than its provider's, but never wider" is what lets
        // a host decide whether an engine can serve a page without paying to build a realm.
        Assert.Equal(realm.Capabilities, realm.Capabilities & provider.Capabilities);

        // A lower-case, hyphenated, stable identifier: it is what a configuration or an environment
        // variable names, so it must not read as a display string.
        Assert.Equal(provider.Name.ToLowerInvariant(), provider.Name);
        Assert.DoesNotContain(' ', provider.Name);
        Assert.False(string.IsNullOrWhiteSpace(provider.Description));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void ADisposedRealmRefusesRatherThanAnsweringForARealmThatIsGone(string engine)
    {
        var realm = NewRealm(engine);
        realm.EnqueueJob(static () => throw new InvalidOperationException("a job of a disposed realm must not run"));
        realm.Dispose();

        // Jobs queued but never drained belong to a realm that is going away; running them would
        // execute page script against a document the host has already finished with.
        Assert.Throws<ObjectDisposedException>(() => realm.DrainJobs());
        Assert.Throws<ObjectDisposedException>(() => realm.EnqueueJob(static () => { }));

        // Dispose is idempotent, because a host with a realm in a using block inside a teardown path
        // will reach it twice.
        realm.Dispose();
    }

    // ── capability coverage ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Which test above demonstrates each capability a provider may declare.
    /// </summary>
    /// <remarks>
    /// A capability is a claim a host is entitled to branch on without verifying it, so a suite that
    /// lets one go unexercised is letting a provider make a claim nothing checks. The meta-test
    /// below reads this table, so a capability added to <see cref="JsCapabilities"/> and declared by
    /// a provider fails the suite until a test for it exists — and the table cannot rot, because the
    /// test names are resolved by reflection rather than trusted.
    /// </remarks>
    private static readonly IReadOnlyDictionary<JsCapabilities, string> CapabilityCoverage =
        new Dictionary<JsCapabilities, string>
        {
            [JsCapabilities.HostScriptSource] = nameof(EvaluatingHostScriptAnswersTheValueOfTheLastExpression),
            [JsCapabilities.GuestEval] = nameof(ARealmBuiltWithoutGuestEvalRefusesGuestSourceAndStillRunsHostScript),
            [JsCapabilities.Promises] = nameof(APromiseSettlesFromTheHostAndItsReactionRunsAtTheNextDrain),
            [JsCapabilities.ExoticObjects] = nameof(AnOrdinaryPropertyWinsOverTheExoticHandler),
            [JsCapabilities.GlobalIsVariableScope] = nameof(ATopLevelDeclarationBecomesAPropertyOfTheGlobal),
            // Covers both halves of what the capability claims: the second realm on the second
            // thread, and values moved between the two by structured clone (IJsClone).
            [JsCapabilities.WorkerRealms] = nameof(ASecondRealmRunsOnASecondThread),
            [JsCapabilities.ReentrantHostCalls] = nameof(AHostFunctionMayCallBackIntoScriptWhileTheEngineIsInsideIt),
        };

    /// <summary>
    /// The capabilities no JSEAL contract can exercise, and why.
    /// </summary>
    /// <remarks>
    /// <b>This is a gap in the contracts, recorded here rather than papered over.</b>
    /// <see cref="JsCapabilities.Modules"/> and <see cref="JsCapabilities.DynamicImport"/> describe
    /// binding an ES-module import end to end, and <see cref="IJsSource"/> — the only contract that
    /// takes source — has no module entry point at all: its two members run a script, which is a
    /// different thing from instantiating and evaluating a module in a module map. So a provider can
    /// declare both and a host written against JSEAL alone has no way to use either, and no way to
    /// tell whether the claim is true. Nothing here can close that; a <c>EvaluateModule</c> on
    /// <see cref="IJsSource"/> would, and until there is one these two stay listed as unexercised
    /// on purpose rather than silently uncovered.
    /// </remarks>
    private static readonly IReadOnlySet<JsCapabilities> NotExpressibleThroughTheContracts =
        new HashSet<JsCapabilities> { JsCapabilities.Modules, JsCapabilities.DynamicImport };

    [Theory]
    [MemberData(nameof(Engines))]
    public void EveryCapabilityAProviderDeclaresIsExercisedBySomeTest(string engine)
    {
        var declared = Provider(engine).Capabilities;

        var unexercised = SingleCapabilities()
            .Where(capability => declared.HasFlag(capability))
            .Where(capability => !CapabilityCoverage.ContainsKey(capability))
            .Where(capability => !NotExpressibleThroughTheContracts.Contains(capability))
            .ToArray();

        Assert.True(
            unexercised.Length == 0,
            $"'{engine}' declares {string.Join(", ", unexercised)}, which no test in this suite exercises. " +
            "Add the test and name it in CapabilityCoverage, or record it in " +
            $"{nameof(NotExpressibleThroughTheContracts)} with the reason no contract reaches it.");
    }

    [Fact]
    public void EveryClaimOfCoverageNamesATestThatActuallyExists()
    {
        // The table above is a claim about this file, so it is checked against this file rather than
        // believed. A test renamed without updating the table would otherwise leave a capability
        // reported as covered by nothing at all.
        foreach (var (capability, testName) in CapabilityCoverage)
        {
            var method = typeof(JsealConformanceTests).GetMethod(
                testName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

            Assert.True(method is not null, $"{capability} names '{testName}', which is not a method of this class.");
            Assert.True(
                method!.GetCustomAttributes(typeof(TheoryAttribute), inherit: false).Length == 1,
                $"{capability} names '{testName}', which is not a [Theory] over the registered providers.");
        }

        // And the two halves must not overlap: a capability cannot be both exercised and declared
        // inexpressible.
        Assert.Empty(CapabilityCoverage.Keys.Intersect(NotExpressibleThroughTheContracts));
    }

    /// <summary>Every capability that names one thing, so the composite <c>Document</c> is skipped.</summary>
    private static IEnumerable<JsCapabilities> SingleCapabilities() =>
        Enum.GetValues<JsCapabilities>().Where(value => BitOperations.PopCount((uint)value) == 1);
}
