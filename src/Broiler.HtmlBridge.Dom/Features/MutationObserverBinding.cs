using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The <c>MutationObserver</c> feature binding module (HtmlBridge complexity-reduction roadmap
/// Phase 3). It owns the observer registry (the Phase 2 <see cref="MutationObserverHub"/> state
/// authority) and co-locates the whole feature: the JS-side <c>MutationObserver</c> polyfill and
/// its host bridge functions, the <c>observe()</c>/<c>disconnect()</c> registration callbacks, the
/// option parsing, and the childList/attribute/characterData record delivery. It depends only on
/// the narrow <see cref="IMutationObserverHost"/> contract (realm + JS-wrapper identity + node
/// lookup); the bridge's mutation path calls the three <c>Deliver…</c> methods, and lifetime reset
/// calls <see cref="Clear"/>.
/// </summary>
/// <remarks>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>), so nothing here names an engine
/// type. Two seams moved rather than disappeared: the polyfill is <em>host</em> script — this
/// repository authored it and a page's Content-Security-Policy has no say over it — so it runs
/// through <see cref="IJsSource.EvaluateHostScript"/> rather than the guest-source entry point; and
/// the two <c>__broiler…MutationObserver</c> host functions are installed on
/// <see cref="IJsRealm.Global"/>, which is the object the script context they were written against
/// already is under this engine.
/// </remarks>
internal sealed class MutationObserverBinding(IMutationObserverHost host)
{
    private readonly IMutationObserverHost _host = host;

    // P2.5 state authority for registered observers (observe() replace semantics, disconnect,
    // snapshot for delivery). Owned here now that the whole feature is co-located.
    private readonly MutationObserverHub _hub = new();

    // The documents whose canonical DomDocument.Mutated we have subscribed to. Delivery is driven
    // off that event (not explicit bridge Notify* calls), so we subscribe lazily the first time an
    // observer targets a node in a given document — the main document and each sub-document (iframe
    // content) uniformly, without hooking document construction. NodeIterator and Range self-manage
    // their own canonical Mutated subscriptions; this is the MutationObserver half.
    private readonly HashSet<DomDocument> _subscribedDocuments = [];

    // -------- Registration --------

    /// <summary>
    /// Installs the JS <c>MutationObserver</c> constructor/prototype and the host bridge functions
    /// (<c>__broilerRegisterMutationObserver</c>/<c>__broilerUnregisterMutationObserver</c>) it
    /// drives.
    /// </summary>
    internal void RegisterDocumentApis()
    {
        var realm = _host.Realm;

        realm.SetProperty(realm.Global, "__broilerRegisterMutationObserver",
            realm.NewMethod("__broilerRegisterMutationObserver", RegisterObserver, 3));
        realm.SetProperty(realm.Global, "__broilerUnregisterMutationObserver",
            realm.NewMethod("__broilerUnregisterMutationObserver", UnregisterObserver, 1));

        // MutationObserver — DOM Level 4
        realm.EvaluateHostScript(MutationObserverPolyfill, "polyfill:mutation-observer");
    }

    /// <summary>
    /// The JS half of the feature: the constructor a page calls, the three prototype operations it
    /// exposes, and the <c>_notify</c> hook the host delivery path invokes. Host script — authored
    /// here, shipped here, and not subject to the page's Content-Security-Policy.
    /// </summary>
    private const string MutationObserverPolyfill = @"
                function MutationObserver(callback) {
                    this._callback = callback;
                    this._targets = [];
                    this._records = [];
                }
                MutationObserver.prototype.observe = function(target, options) {
                    var normalizedOptions = options || {};
                    this._targets.push({ target: target, options: normalizedOptions });
                    if (typeof __broilerRegisterMutationObserver === 'function') {
                        __broilerRegisterMutationObserver(this, target, normalizedOptions);
                    }
                };
                MutationObserver.prototype.disconnect = function() {
                    this._targets = [];
                    this._records = [];
                    if (typeof __broilerUnregisterMutationObserver === 'function') {
                        __broilerUnregisterMutationObserver(this);
                    }
                };
                MutationObserver.prototype.takeRecords = function() {
                    var r = this._records.slice();
                    this._records = [];
                    return r;
                };
                MutationObserver.prototype._notify = function(records) {
                    if (records && records.length > 0) {
                        for (var i = 0; i < records.length; i++) {
                            this._records.push(records[i]);
                        }
                        var pending = this._records.slice();
                        this._records = [];
                        try { this._callback(pending, this); } catch(e) {}
                    }
                };
            ";

    private JsValue RegisterObserver(in JsCall call)
    {
        if (call.Length < 2 || !call[0].IsObject || !call[1].IsObject)
            return JsValue.Undefined;
        // A MutationObserver can observe a character-data node (characterData mutations).
        var target = _host.FindNode(call[1]);
        if (target == null)
            return JsValue.Undefined;
        EnsureSubscribed(target.OwnerDocument);
        // call[2] is Missing when observe() passed no options — which CreateMutationObserverOptions
        // reads as "not an object" and answers with the all-false default, exactly as the explicit
        // `a.Length > 2 ? a[2] : undefined` did.
        _hub.Register(call[0], target, CreateMutationObserverOptions(call.Realm, call[2]));
        return JsValue.Undefined;
    }

    // -------- Canonical mutation subscription --------

    /// <summary>Subscribes to a document's <see cref="DomDocument.Mutated"/> once (idempotent).</summary>
    private void EnsureSubscribed(DomDocument document)
    {
        if (_subscribedDocuments.Add(document))
            document.Mutated += OnDocumentMutation;
    }

    /// <summary>
    /// Translates a canonical mutation record into the feature's childList/attribute/characterData
    /// delivery. Suppressed while the bridge mutates the live tree internally (serialize/render
    /// bakes, re-parse) so those implementation-detail mutations are not delivered to script (and
    /// cannot re-enter script mid-serialize). Delivery is synchronous, matching the pre-existing
    /// observer model (a script reads its record log on the line after the mutation).
    /// </summary>
    private void OnDocumentMutation(DomMutationRecord record)
    {
        if (_hub.Count == 0 || _host.MutationDeliverySuppressed)
            return;

        switch (record.Type)
        {
            case DomMutationType.ChildList:
                if (record.AddedNodes is { Count: > 0 } added)
                    foreach (var node in added)
                        DeliverChildListMutation(record.Target, node, null, record.PreviousSibling, record.NextSibling);
                if (record.RemovedNodes is { Count: > 0 } removed)
                    foreach (var node in removed)
                        DeliverChildListMutation(record.Target, null, node, record.PreviousSibling, record.NextSibling);
                break;
            case DomMutationType.Attributes when record.Target is DomElement element && record.AttributeName is { } attributeName:
                // The bridge reports "" (not null) as the prior value of a newly-added attribute
                // (its TryGetAttribute default); canonical publishes null. Coalesce to preserve the
                // characterized observer oldValue behaviour.
                DeliverAttributeMutation(element, attributeName, record.OldValue ?? string.Empty);
                break;
            case DomMutationType.CharacterData:
                DeliverCharacterDataMutation(record.Target, record.OldValue);
                break;
        }
    }

    private JsValue UnregisterObserver(in JsCall call)
    {
        if (call.Length > 0 && call[0].IsObject)
            _hub.Unregister(call[0]);
        return JsValue.Undefined;
    }

    /// <summary>
    /// One boolean member of a <c>MutationObserverInit</c>. Absent, <c>null</c> and <c>undefined</c>
    /// all read false; anything else is ECMAScript truthiness, which the handle decides without
    /// entering the engine — the same answer <c>BooleanValue</c> gave.
    /// </summary>
    private static bool GetMutationObserverOption(IJsRealm realm, JsValue optionsObject, string propertyName)
    {
        var optionValue = realm.GetProperty(optionsObject, propertyName);
        return !optionValue.IsNullish && optionValue.AsBoolean;
    }

    private static DomMutationObserverOptions CreateMutationObserverOptions(IJsRealm realm, JsValue value)
    {
        if (!value.IsObject)
            return new DomMutationObserverOptions();

        return new DomMutationObserverOptions
        {
            ChildList = GetMutationObserverOption(realm, value, "childList"),
            Attributes = GetMutationObserverOption(realm, value, "attributes"),
            AttributeOldValue = GetMutationObserverOption(realm, value, "attributeOldValue"),
            CharacterData = GetMutationObserverOption(realm, value, "characterData"),
            CharacterDataOldValue = GetMutationObserverOption(realm, value, "characterDataOldValue"),
            Subtree = GetMutationObserverOption(realm, value, "subtree")
        };
    }

    // -------- Record delivery --------

    /// <summary>Delivers a <c>childList</c> mutation record to every matching registered observer.</summary>
    internal void DeliverChildListMutation(DomNode target,
        DomNode? addedChild, DomNode? removedChild, DomNode? previousSibling, DomNode? nextSibling)
    {
        if (_hub.Count == 0)
            return;

        var realm = _host.Realm;
        var mutation = new DomMutationRecord(DomMutationType.ChildList, target);
        foreach (var (observer, observedTarget, options) in _hub.Snapshot())
        {
            if (!DomMutationObserverFilter.Matches(mutation, observedTarget, options))
                continue;

            var notifyFunction = realm.GetProperty(observer, "_notify");
            if (!notifyFunction.IsFunction)
                continue;

            var record = realm.NewObject();
            realm.SetProperty(record, "type", JsValue.String("childList"));
            realm.SetProperty(record, "target", _host.WrapNode(target));
            realm.SetProperty(record, "addedNodes", addedChild != null
                ? realm.NewArray([_host.WrapNode(addedChild)])
                : realm.NewArray());
            realm.SetProperty(record, "removedNodes", removedChild != null
                ? realm.NewArray([_host.WrapNode(removedChild)])
                : realm.NewArray());
            realm.SetProperty(record, "previousSibling", previousSibling != null
                ? _host.WrapNode(previousSibling)
                : JsValue.Null);
            realm.SetProperty(record, "nextSibling", nextSibling != null
                ? _host.WrapNode(nextSibling)
                : JsValue.Null);

            realm.Invoke(notifyFunction, observer, [realm.NewArray([record])]);
        }
    }

    /// <summary>Delivers an <c>attributes</c> mutation record to every matching registered observer.</summary>
    internal void DeliverAttributeMutation(DomElement target, string attributeName, string? oldValue)
    {
        if (_hub.Count == 0)
            return;

        var realm = _host.Realm;
        var mutation = new DomMutationRecord(DomMutationType.Attributes, target, AttributeName: attributeName);
        foreach (var (observer, observedTarget, options) in _hub.Snapshot())
        {
            if (!DomMutationObserverFilter.Matches(mutation, observedTarget, options))
                continue;

            var notifyFunction = realm.GetProperty(observer, "_notify");
            if (!notifyFunction.IsFunction)
                continue;

            var record = realm.NewObject();
            realm.SetProperty(record, "type", JsValue.String("attributes"));
            realm.SetProperty(record, "target", _host.WrapNode(target));
            realm.SetProperty(record, "attributeName", JsValue.String(attributeName));
            realm.SetProperty(record, "oldValue", options.AttributeOldValue && oldValue != null
                ? JsValue.String(oldValue)
                : JsValue.Null);

            realm.Invoke(notifyFunction, observer, [realm.NewArray([record])]);
        }
    }

    /// <summary>Delivers a <c>characterData</c> mutation record to every matching registered observer.</summary>
    internal void DeliverCharacterDataMutation(DomNode target, string? oldValue)
    {
        if (_hub.Count == 0)
            return;

        var realm = _host.Realm;
        var mutation = new DomMutationRecord(DomMutationType.CharacterData, target);
        foreach (var (observer, observedTarget, options) in _hub.Snapshot())
        {
            if (!DomMutationObserverFilter.Matches(mutation, observedTarget, options))
                continue;

            var notifyFunction = realm.GetProperty(observer, "_notify");
            if (!notifyFunction.IsFunction)
                continue;

            var record = realm.NewObject();
            realm.SetProperty(record, "type", JsValue.String("characterData"));
            realm.SetProperty(record, "target", _host.WrapNode(target));
            realm.SetProperty(record, "oldValue", options.CharacterDataOldValue && oldValue != null
                ? JsValue.String(oldValue)
                : JsValue.Null);

            realm.Invoke(notifyFunction, observer, [realm.NewArray([record])]);
        }
    }

    /// <summary>Drops all registered observers and canonical subscriptions (session reset/dispose).</summary>
    internal void Clear()
    {
        foreach (var document in _subscribedDocuments)
            document.Mutated -= OnDocumentMutation;
        _subscribedDocuments.Clear();
        _hub.Clear();
    }
}
