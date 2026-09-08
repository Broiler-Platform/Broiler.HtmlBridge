using Broiler.CSS;
using Broiler.Dom;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.HtmlBridge.Jseal;

// Engine-typed only for the three adapters at the foot of this file. Their callers are the two
// registration hubs — DomBridge/Registration/Registration.cs installs the CSSStyleSheet constructor and
// DomBridge/Registration/Document.cs installs document.adoptedStyleSheets — and neither is owned this
// round, so their call frames and value types stay as they are.
using Broiler.JavaScript.BuiltIns.Array;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge;

/// <summary>
/// Constructable stylesheets (CSSOM) — <c>new CSSStyleSheet()</c>, its
/// <c>insertRule</c>/<c>deleteRule</c>/<c>replaceSync</c>/<c>replace</c> surface, and
/// <c>document.adoptedStyleSheets</c>. A constructed sheet is not tied to a
/// <c>&lt;style&gt;</c>/<c>&lt;link&gt;</c> element; it carries its own rule list and applies
/// to the document only while it is in <c>adoptedStyleSheets</c>. At serialization the adopted
/// sheets are emitted as synthetic <c>&lt;style&gt;</c> elements appended after the document's
/// own stylesheets, so the renderer applies them in the correct cascade order (the WPT
/// <c>css/cssom</c> constructable family, e.g.
/// <c>CSSStyleSheet-constructable-insertRule-base-uri</c>, which adopts a sheet whose rule sets
/// a <c>background-image</c>).
/// </summary>
public sealed partial class DomBridge
{
    /// <summary>The live <c>document.adoptedStyleSheets</c> array (constructed sheets in
    /// application order). Lazily created; <see cref="JsValue.Missing"/> until first read.</summary>
    private JsValue _adoptedStyleSheets;

    /// <summary>Rule list backing each constructed <c>CSSStyleSheet</c> object, so the
    /// serialization pass can emit the adopted sheets' rules.</summary>
    /// <remarks>
    /// Keyed on the JSEAL handle rather than on the engine object, which is the same key: a handle over
    /// an object compares by the object's reference (<see cref="JsValue.Equals(JsValue)"/>), so a sheet
    /// looked up here is found by the identity it always was found by.
    /// </remarks>
    private readonly Dictionary<JsValue, List<CssRule>> _constructedSheetRules = [];

    private JsValue BuildConstructedStyleSheetObject(List<CssRule> rules)
    {
        var realm = Realm;
        var sheet = realm.NewObject();
        _constructedSheetRules[sheet] = rules;

        List<CssRule> CurrentRules() => rules;
        // A constructed sheet has no owner element and so no StyleSheetRuntimeState to mark — there
        // is nothing to reparse from, the list *is* the sheet. It still reaches the cascade through
        // adoptedStyleSheets, so an insertRule/deleteRule on it is a layout change no DOM mutation
        // records: move the epoch so a retained geometry snapshot is not answered from the pre-edit
        // rules. See BridgeRuntimeStateEpoch.
        static void MarkRulesMutated() => BridgeRuntimeStateEpoch.Bump();

        // ownerNode is null for a constructed sheet; href is null (no source URL).
        realm.DefineAccessor(sheet, "ownerNode", (in _) => JsValue.Null, null);
        realm.DefineAccessor(sheet, "href", (in _) => JsValue.Null, null);

        // disabled — a script flag on the sheet; a disabled adopted sheet does not apply.
        var disabled = false;
        realm.DefineAccessor(sheet, "disabled",
            (in _) => JsValue.Boolean(disabled),
            (in call) => { disabled = call.Length > 0 && call[0].AsBoolean; return JsValue.Undefined; });

        var liveCssRules = realm.NewObject();
        var lastSyncedRuleCount = 0;
        realm.DefineAccessor(liveCssRules, "length",
            (in _) => Dom.Features.StyleSheetBinding.JsStyleSheetsGetLength002Core(CurrentRules), null);
        realm.DefineValue(liveCssRules, "item",
            realm.NewMethod("item",
                (in call) => Dom.Features.StyleSheetBinding.JsStyleSheetsItem003Core(SyncLiveCssRulesIndices, liveCssRules, CurrentRules, in call), 1));

        void SyncLiveCssRulesIndices()
        {
            var current = CurrentRules();
            for (var i = 0; i < current.Count; i++)
                realm.DefineIndex(liveCssRules, (uint)i, Dom.Features.StyleSheetBinding.BuildCssRuleObject(realm, current[i], sheet));

            // Retiring an index is the one CSSOM operation JSEAL cannot express; see
            // StyleSheetBinding.RetireIndex, which is where the reasoning lives.
            for (var i = current.Count; i < lastSyncedRuleCount; i++)
                Dom.Features.StyleSheetBinding.RetireIndex(liveCssRules, (uint)i);

            lastSyncedRuleCount = current.Count;
        }

        realm.DefineAccessor(sheet, "cssRules",
            (in _) => Dom.Features.StyleSheetBinding.JsStyleSheetsGetCssRules004Core(SyncLiveCssRulesIndices, liveCssRules), null);
        realm.DefineValue(sheet, "insertRule",
            realm.NewMethod("insertRule",
                (in call) => Dom.Features.StyleSheetBinding.JsStyleSheetsInsertRule005Core(CurrentRules, MarkRulesMutated, SyncLiveCssRulesIndices, in call), 2));
        realm.DefineValue(sheet, "deleteRule",
            realm.NewMethod("deleteRule",
                (in call) => Dom.Features.StyleSheetBinding.JsStyleSheetsDeleteRule006Core(CurrentRules, MarkRulesMutated, SyncLiveCssRulesIndices, in call), 1));

        // replaceSync(text) — replace all rules from a CSS string (any @import is dropped per
        // spec). replace(text) does the same and returns an already-resolved promise of the sheet.
        void ReplaceFromText(string text)
        {
            rules.Clear();
            foreach (var rule in new CssParser().ParseStyleSheet(text).Rules)
            {
                if (rule is CssAtRule at && at.Name.Equals("import", StringComparison.OrdinalIgnoreCase))
                    continue;
                rules.Add(rule);
            }
            SyncLiveCssRulesIndices();
        }

        realm.DefineValue(sheet, "replaceSync",
            realm.NewMethod("replaceSync", (in call) =>
            {
                ReplaceFromText(call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty);
                return JsValue.Undefined;
            }, 1));
        realm.DefineValue(sheet, "replace",
            realm.NewMethod("replace", (in call) =>
            {
                ReplaceFromText(call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty);
                return ResolvedThenableWith(sheet);
            }, 1));

        return sheet;
    }

    /// <summary>An already-resolved thenable that yields <paramref name="value"/> — the
    /// promise <c>CSSStyleSheet.replace()</c> returns (resolving with the sheet itself).</summary>
    /// <remarks>
    /// A hand-built thenable rather than <see cref="IJsJobs.NewPromise"/>, deliberately and unchanged:
    /// the callback runs synchronously at <c>then()</c> rather than at a microtask checkpoint, which is
    /// what the render path this feeds depends on. Moving it to a real promise would be a behaviour
    /// change, not a migration.
    /// </remarks>
    private JsValue ResolvedThenableWith(JsValue value)
    {
        var realm = Realm;
        var thenable = realm.NewObject();

        JsValue Then(in JsCall call)
        {
            if (call.Length > 0 && call[0].IsFunction)
            {
                // The callback is its own receiver, which is what the engine-typed call frame passed.
                try { call.Realm.Invoke(call[0], call[0], [value]); }
                catch { /* a replace().then callback must not abort the render */ }
            }
            return thenable;
        }

        realm.DefineValue(thenable, "then", realm.NewMethod("then", Then, 1));
        realm.DefineValue(thenable, "catch", realm.NewMethod("catch", (in _) => thenable, 1));
        realm.DefineValue(thenable, "finally",
            realm.NewMethod("finally", (in call) =>
            {
                if (call.Length > 0 && call[0].IsFunction)
                {
                    try { call.Realm.Invoke(call[0], call[0]); }
                    catch { /* as above */ }
                }
                return thenable;
            }, 1));

        return thenable;
    }

    /// <summary>The live <c>document.adoptedStyleSheets</c> array, created on first access.</summary>
    private JsValue AdoptedStyleSheets() =>
        _adoptedStyleSheets.IsObject ? _adoptedStyleSheets : (_adoptedStyleSheets = Realm.NewArray());

    /// <summary>
    /// Emits each adopted stylesheet as a synthetic <c>&lt;style&gt;</c> appended after the
    /// document's own stylesheets, so the renderer applies the adopted rules in cascade order.
    /// A no-op when nothing is adopted. Runs from <see cref="ApplySerializationTransforms"/>.
    /// </summary>
    /// <remarks>
    /// The walk is by index rather than by the engine's hole-skipping enumerator, which reaches the same
    /// sheets: a hole reads as <c>undefined</c>, and the very next test — is this an object the
    /// constructed-sheet table knows — rejects it exactly as the enumerator's skip did.
    /// </remarks>
    private void ApplyAdoptedStyleSheets(DomElement root)
    {
        if (!_adoptedStyleSheets.IsObject)
            return;

        var realm = Realm;
        // Array length is a number by construction, so the handle carries it and no coercion is needed.
        var length = realm.GetProperty(_adoptedStyleSheets, "length").AsNumber;
        if (!(length > 0))
            return;

        var head = FindFirstElementByTagName(root, "head");
        var container = head ?? root;

        for (var i = 0u; i < length; i++)
        {
            var item = realm.GetIndex(_adoptedStyleSheets, i);
            if (!item.IsObject ||
                !_constructedSheetRules.TryGetValue(item, out var rules) ||
                rules.Count == 0)
                continue;

            if (realm.GetProperty(item, "disabled").AsBoolean)
                continue;

            var css = string.Join("\n", rules.Select(CssSerializer.Serialize));
            var styleElement = CreateBridgeElement("style");
            SetElementTextContent(styleElement, css);
            SetParent(styleElement, container);
            container.AppendChild(styleElement);
        }
    }

    // -------- engine-typed adapters (see the note at the head of this file) --------

    /// <summary><c>new CSSStyleSheet(options)</c> — an empty constructed stylesheet. Options
    /// (media/disabled/baseURL) are accepted but not yet modelled; a constructed sheet's rules
    /// resolve relative <c>url()</c>s against the document base at render time.</summary>
    private JSObject CreateConstructedStyleSheet(in Arguments a) =>
        JsInterop.ToEngineObject(BuildConstructedStyleSheetObject([]));

    /// <summary>The live <c>document.adoptedStyleSheets</c> array, as its unmigrated getter reads it.</summary>
    private JSObject AdoptedStyleSheetsArray() => JsInterop.ToEngineObject(AdoptedStyleSheets());

    /// <summary>Replaces <c>document.adoptedStyleSheets</c> with the assigned array's members
    /// (<c>document.adoptedStyleSheets = [sheet, …]</c>); <c>.push()</c> on the getter's array
    /// is handled directly by the returned array.</summary>
    /// <remarks>
    /// The copy is made in engine terms because the assigned value arrives that way and every element of
    /// it — sheet objects and whatever else a page assigned — has to survive the round trip byte for
    /// byte. The handle stored is over the array this builds, so the migrated readers above see it.
    /// </remarks>
    private void SetAdoptedStyleSheets(JSValue value)
    {
        var replacement = new JSArray();
        if (value is JSArray array)
            foreach (var (_, item) in array.GetArrayElements(withHoles: false))
                replacement.Add(item);

        _adoptedStyleSheets = JsInterop.FromEngineObject(replacement);
    }
}
