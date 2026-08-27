using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Array;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.Storage;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Engine;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The <c>MutationObserver</c> feature binding module (HtmlBridge complexity-reduction roadmap
/// Phase 3). It owns the observer registry (the Phase 2 <see cref="MutationObserverHub"/> state
/// authority) and co-locates the whole feature: the JS-side <c>MutationObserver</c> polyfill and
/// its host bridge functions, the <c>observe()</c>/<c>disconnect()</c> registration callbacks, the
/// option parsing, and the childList/attribute/characterData record delivery. It depends only on
/// the narrow <see cref="IMutationObserverHost"/> contract (JS-wrapper identity + node lookup); the
/// bridge's mutation path calls the three <c>Deliver…</c> methods, and lifetime reset calls
/// <see cref="Clear"/>.
/// </summary>
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
    internal void RegisterDocumentApis(JSContext context)
    {
        var registerMutationObserverFn = new DomFunction((in a) => RegisterObserver(in a), "__broilerRegisterMutationObserver", 3);
        var unregisterMutationObserverFn = new DomFunction((in a) => UnregisterObserver(in a), "__broilerUnregisterMutationObserver", 1);
        context["__broilerRegisterMutationObserver"] = registerMutationObserverFn;
        context["__broilerUnregisterMutationObserver"] = unregisterMutationObserverFn;
        // MutationObserver — DOM Level 4
        context.Eval(@"
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
            ");
    }

    private JSValue RegisterObserver(in Arguments a)
    {
        if (a.Length < 2 || a[0] is not JSObject observerObject || a[1] is not JSObject targetObject)
            return JSUndefined.Value;
        // A MutationObserver can observe a character-data node (characterData mutations).
        var target = _host.FindDomNodeByJSObject(targetObject);
        if (target == null)
            return JSUndefined.Value;
        EnsureSubscribed(target.OwnerDocument);
        _hub.Register(observerObject, target, CreateMutationObserverOptions(a.Length > 2 ? a[2] : JSUndefined.Value));
        return JSUndefined.Value;
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

    private JSValue UnregisterObserver(in Arguments a)
    {
        if (a.Length > 0 && a[0] is JSObject observerObject)
            _hub.Unregister(observerObject);
        return JSUndefined.Value;
    }

    private static bool GetMutationObserverOption(JSObject optionsObject, string propertyName)
    {
        var optionValue = optionsObject[(KeyString)propertyName];
        return optionValue != null &&
               !optionValue.IsUndefined &&
               !optionValue.IsNull &&
               optionValue.BooleanValue;
    }

    private static DomMutationObserverOptions CreateMutationObserverOptions(JSValue? value)
    {
        if (value is not JSObject optionsObject)
            return new DomMutationObserverOptions();

        return new DomMutationObserverOptions
        {
            ChildList = GetMutationObserverOption(optionsObject, "childList"),
            Attributes = GetMutationObserverOption(optionsObject, "attributes"),
            AttributeOldValue = GetMutationObserverOption(optionsObject, "attributeOldValue"),
            CharacterData = GetMutationObserverOption(optionsObject, "characterData"),
            CharacterDataOldValue = GetMutationObserverOption(optionsObject, "characterDataOldValue"),
            Subtree = GetMutationObserverOption(optionsObject, "subtree")
        };
    }

    // -------- Record delivery --------

    /// <summary>Delivers a <c>childList</c> mutation record to every matching registered observer.</summary>
    internal void DeliverChildListMutation(DomNode target,
        DomNode? addedChild, DomNode? removedChild, DomNode? previousSibling, DomNode? nextSibling)
    {
        if (_hub.Count == 0)
            return;

        var mutation = new DomMutationRecord(DomMutationType.ChildList, target);
        foreach (var (observer, observedTarget, options) in _hub.Snapshot())
        {
            if (!DomMutationObserverFilter.Matches(mutation, observedTarget, options))
                continue;

            if (observer[(KeyString)"_notify"] is not JSFunction notifyFunction)
                continue;

            var record = new JSObject();
            record[(KeyString)"type"] = new JSString("childList");
            record[(KeyString)"target"] = _host.ToJSObject(target);
            record[(KeyString)"addedNodes"] = addedChild != null
                ? new JSArray([_host.ToJSObject(addedChild)])
                : new JSArray([]);
            record[(KeyString)"removedNodes"] = removedChild != null
                ? new JSArray([_host.ToJSObject(removedChild)])
                : new JSArray([]);
            record[(KeyString)"previousSibling"] = previousSibling != null
                ? _host.ToJSObject(previousSibling)
                : JSNull.Value;
            record[(KeyString)"nextSibling"] = nextSibling != null
                ? _host.ToJSObject(nextSibling)
                : JSNull.Value;

            notifyFunction.InvokeFunction(new Arguments(observer, new JSArray([record])));
        }
    }

    /// <summary>Delivers an <c>attributes</c> mutation record to every matching registered observer.</summary>
    internal void DeliverAttributeMutation(DomElement target, string attributeName, string? oldValue)
    {
        if (_hub.Count == 0)
            return;

        var mutation = new DomMutationRecord(DomMutationType.Attributes, target, AttributeName: attributeName);
        foreach (var (observer, observedTarget, options) in _hub.Snapshot())
        {
            if (!DomMutationObserverFilter.Matches(mutation, observedTarget, options))
                continue;

            if (observer[(KeyString)"_notify"] is not JSFunction notifyFunction)
                continue;

            var record = new JSObject();
            record[(KeyString)"type"] = new JSString("attributes");
            record[(KeyString)"target"] = _host.ToJSObject(target);
            record[(KeyString)"attributeName"] = new JSString(attributeName);
            record[(KeyString)"oldValue"] = options.AttributeOldValue && oldValue != null
                ? new JSString(oldValue)
                : JSNull.Value;

            notifyFunction.InvokeFunction(new Arguments(observer, new JSArray([record])));
        }
    }

    /// <summary>Delivers a <c>characterData</c> mutation record to every matching registered observer.</summary>
    internal void DeliverCharacterDataMutation(DomNode target, string? oldValue)
    {
        if (_hub.Count == 0)
            return;

        var mutation = new DomMutationRecord(DomMutationType.CharacterData, target);
        foreach (var (observer, observedTarget, options) in _hub.Snapshot())
        {
            if (!DomMutationObserverFilter.Matches(mutation, observedTarget, options))
                continue;

            if (observer[(KeyString)"_notify"] is not JSFunction notifyFunction)
                continue;

            var record = new JSObject();
            record[(KeyString)"type"] = new JSString("characterData");
            record[(KeyString)"target"] = _host.ToJSObject(target);
            record[(KeyString)"oldValue"] = options.CharacterDataOldValue && oldValue != null
                ? new JSString(oldValue)
                : JSNull.Value;

            notifyFunction.InvokeFunction(new Arguments(observer, new JSArray([record])));
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
