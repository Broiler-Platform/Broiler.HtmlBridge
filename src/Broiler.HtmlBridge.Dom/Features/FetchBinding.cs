using System.Text;

using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.HtmlBridge.Internal.Scripting;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The networking feature binding module (HtmlBridge complexity-reduction roadmap Phase 3, P3.11).
/// It co-locates the whole <c>fetch</c> / <c>XMLHttpRequest</c> surface: the <c>fetch</c> polyfill and
/// its <c>Headers</c>/<c>Request</c>/<c>Response</c>/<c>FormData</c>/<c>Blob</c>/<c>AbortController</c>
/// helper objects, the <c>Response</c> static factories and the <c>XMLHttpRequest</c> polyfill. Host
/// I/O goes through the injected Phase 2 <see cref="ResourceLoader"/> — the "no feature callback
/// constructs an <c>HttpClient</c>" seam Phase 7 builds on — and the other bridge couplings (the page
/// URL used to resolve <c>Response.redirect</c> relative URLs, the realm, and the blob/stream objects
/// other modules own) are reached through the narrow <see cref="IFetchHost"/> contract. The
/// non-networking registrations that historically lived in this method (<c>MessageChannel</c>,
/// <c>getComputedStyle</c>) were moved back to the window-globals registration site.
/// </summary>
/// <remarks>
/// <para>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>). One thing here cannot be said in
/// it and is named as such where it occurs: an <c>ArrayBuffer</c>, which <see cref="IJsValues"/> has
/// no member for — the same line <c>StreamsBinding</c> and <c>BlobBinding</c> record.
/// </para>
/// <para>
/// <b>Every member of these objects is installed as a constructable function, and that is preserved
/// rather than fixed.</b> The surface was built with <c>JSFunction</c> throughout — not the bridge's
/// <c>DomFunction</c> — so <c>headers.get.prototype</c> is an object and <c>new headers.get()</c>
/// does not throw, where a browser's <c>Headers.prototype.get</c> is not constructable. That is a
/// pre-existing deviation, and <see cref="IJsValues.NewConstructor"/> is the faithful spelling of it;
/// migrating it to <see cref="IJsValues.NewMethod"/> would have been a behaviour change smuggled into
/// a refactor.
/// </para>
/// </remarks>
internal sealed partial class FetchBinding(IFetchHost host, ResourceLoader resources)
{
    private readonly IFetchHost _host = host;
    private readonly ResourceLoader _resources = resources;

    /// <summary>
    /// The realm's own <c>JSON.parse</c> and <c>JSON.stringify</c>, read once when the surface is
    /// installed.
    /// </summary>
    /// <remarks>
    /// The two were the engine's <c>JSJSON</c> statics, which are the intrinsics and cannot be
    /// reached through the page's <c>globalThis.JSON</c>. Reading them at registration — before a
    /// page's first script runs — is what keeps that true through a contract that has no JSON
    /// member: a page that later replaces <c>JSON</c> changes what its own code sees and not what a
    /// <c>response.json()</c> does, exactly as before.
    /// </remarks>
    private JsValue _jsonParse;

    private JsValue _jsonStringify;

    private delegate string? JsPropertyStringGetter(JsValue obj, params string[] names);

    private delegate IEnumerable<(string Key, string Value)> ObjectStringEntriesEnumerator(JsValue obj);

    private delegate (int status, string statusText, string url, string type, bool redirected, Dictionary<string, string> headers) ResponseInitParser(JsValue initValue);

    private delegate JsValue ResponseFactory(string body, int statusCode, string statusText,
        string responseUrl, string type, bool redirected, Dictionary<string, string> headers);

    /// <summary>Installs <c>fetch</c>/<c>Headers</c>/<c>Request</c>/<c>Response</c>/<c>FormData</c> and
    /// <c>XMLHttpRequest</c> on <paramref name="window"/> and the realm's global, returning the
    /// <c>fetch</c> function so the caller can register it among the window globals.</summary>
    internal JsValue Install(IJsRealm realm, JsValue window)
    {
        var json = realm.GetProperty(realm.Global, "JSON");
        _jsonParse = realm.GetProperty(json, "parse");
        _jsonStringify = realm.GetProperty(json, "stringify");

        IEnumerable<(string Key, string Value)> EnumerateObjectStringEntries(JsValue obj)
        {
            foreach (var key in realm.OwnPropertyNames(obj))
            {
                var value = realm.GetProperty(obj, key);
                if (string.IsNullOrEmpty(key) || key[0] == '_' || value.IsFunction || value.IsUndefined || value.IsNull)
                    continue;

                // The realm's ToString, not the handle's: this is the observable ECMAScript coercion
                // the engine's own ToString() performed here, and a header value that is an object
                // with a toString is entitled to it.
                yield return (key, realm.ToJsString(value));
            }
        }
        string? TryGetJsPropertyString(JsValue obj, params string[] names)
        {
            foreach (var name in names)
            {
                // IsNullish is the three tests this made before it coerced — absent (the engine's
                // indexer answered a CLR null, which is Missing here), undefined, and null.
                var value = realm.GetProperty(obj, name);
                if (!value.IsNullish)
                    return realm.ToJsString(value);
            }

            return null;
        }
        /// <summary>
        /// A settled native Promise for a body value that is already in hand — what
        /// <c>response.text()</c>, <c>.json()</c>, <c>.arrayBuffer()</c>, <c>.blob()</c>,
        /// <c>.formData()</c> and a stream reader's <c>read()</c> return.
        /// </summary>
        /// <remarks>
        /// This used to be a hand-rolled object carrying one <c>then</c> that invoked the callback and
        /// returned <b>itself</b>. Returning itself is what broke chaining: <c>.then(a).then(b)</c> ran
        /// <c>b</c> against the ORIGINAL value rather than <c>a</c>'s result, so a mapping chain read
        /// the unmapped value — a silently wrong answer rather than an error. It also had no
        /// <c>catch</c> and no <c>finally</c> (so <c>.finally()</c> was a TypeError), was not
        /// <c>instanceof Promise</c>, and had no rejection path at all, so a resolver that threw — a
        /// <c>.json()</c> over a malformed body, say — threw synchronously out of <c>.then</c> instead
        /// of rejecting the promise.
        /// <para>
        /// A real promise fixes all of it at once, and the engine's microtask queue is pumped in a
        /// capture (a plain <c>Promise.resolve().then(...)</c> callback runs), so settling through the
        /// real machinery still delivers the callback.
        /// </para>
        /// <para>
        /// The realm hands back the two settle functions rather than running an executor, so the
        /// promise is settled here instead of inside a callback that only happened to run
        /// synchronously — the difference <see cref="IJsJobs.NewPromise"/> exists to remove. The
        /// executor's <c>try</c> came with that shape and is written out: a throwing
        /// <paramref name="resolver"/> rejects, which is the conforming outcome and what the JSON
        /// body readers rest on.
        /// </para>
        /// </remarks>
        JsValue CreateThenable(Func<JsValue> resolver)
        {
            var promise = realm.NewPromise(out var resolve, out var reject);

            try
            {
                resolve(resolver());
            }
            catch (JsEngineException ex) when (!ex.Thrown.IsMissing)
            {
                // Something the page threw — a toJSON, a getter — reaches the rejection as the value
                // it threw rather than as a description of it.
                reject(ex.Thrown);
            }
            catch (Exception ex)
            {
                reject(ErrorValue(realm, ex.Message));
            }

            return promise;
        }
        JsValue CreateHeadersObject(JsValue initValue = default)
        {
            var headersObject = realm.NewObject();
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var originalNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            void SyncHeader(string name)
            {
                if (!values.TryGetValue(name, out var currentValue))
                    currentValue = string.Empty;

                var originalName = originalNames.TryGetValue(name, out var storedName) ? storedName : name;
                realm.SetProperty(headersObject, originalName, JsValue.String(currentValue));
                realm.SetProperty(headersObject, name.ToLowerInvariant(), JsValue.String(currentValue));
            }

            void SetHeader(string name, string value)
            {
                values[name] = value;
                originalNames[name] = name;
                SyncHeader(name);
            }

            void AppendHeader(string name, string value)
            {
                if (values.TryGetValue(name, out var existing) && !string.IsNullOrEmpty(existing))
                    values[name] = $"{existing}, {value}";
                else
                    values[name] = value;

                originalNames[name] = name;
                SyncHeader(name);
            }

            if (initValue.IsObject)
            {
                foreach (var (key, value) in EnumerateObjectStringEntries(initValue))
                    AppendHeader(key, value);
            }
            JsValue JsRegistrationGet078(in JsCall call)
            {
                if (call.Length == 0)
                    return JsValue.Null;
                var name = call.Realm.ToJsString(call[0]);
                return values.TryGetValue(name, out var currentValue) ? JsValue.String(currentValue) : JsValue.Null;
            }

            realm.DefineValue(headersObject, "get", realm.NewConstructor("get", JsRegistrationGet078, 1));
            JsValue JsRegistrationHas079(in JsCall call)
            {
                if (call.Length == 0)
                    return JsValue.False;
                return JsValue.Boolean(values.ContainsKey(call.Realm.ToJsString(call[0])));
            }
            realm.DefineValue(headersObject, "has", realm.NewConstructor("has", JsRegistrationHas079, 1));
            JsValue JsRegistrationSet080(in JsCall call)
            {
                if (call.Length >= 2)
                    SetHeader(call.Realm.ToJsString(call[0]), call.Realm.ToJsString(call[1]));
                return JsValue.Undefined;
            }
            realm.DefineValue(headersObject, "set", realm.NewConstructor("set", JsRegistrationSet080, 2));
            JsValue JsRegistrationAppend081(in JsCall call)
            {
                if (call.Length >= 2)
                    AppendHeader(call.Realm.ToJsString(call[0]), call.Realm.ToJsString(call[1]));
                return JsValue.Undefined;
            }
            realm.DefineValue(headersObject, "append", realm.NewConstructor("append", JsRegistrationAppend081, 2));
            JsValue JsRegistrationDelete082(in JsCall call)
            {
                if (call.Length > 0)
                {
                    var name = call.Realm.ToJsString(call[0]);
                    values.Remove(name);
                    originalNames.Remove(name);
                    realm.SetProperty(headersObject, name, JsValue.Undefined);
                    realm.SetProperty(headersObject, name.ToLowerInvariant(), JsValue.Undefined);
                }

                return JsValue.Undefined;
            }
            realm.DefineValue(headersObject, "delete", realm.NewConstructor("delete", JsRegistrationDelete082, 1));
            JsValue JsRegistrationForEach083(in JsCall call)
            {
                if (call.Length > 0 && call[0].IsFunction)
                {
                    var callback = call[0];
                    foreach (var header in values)
                    {
                        var name = originalNames.TryGetValue(header.Key, out var originalName) ? originalName : header.Key;

                        // The receiver stays the callback itself, as it was: the frame this used to be
                        // built with passed the function as `this`, which is not what the
                        // specification says and is not this migration's to change.
                        realm.Invoke(callback, callback,
                            [JsValue.String(header.Value), JsValue.String(name), headersObject]);
                    }
                }

                return JsValue.Undefined;
            }
            realm.DefineValue(headersObject, "forEach", realm.NewConstructor("forEach", JsRegistrationForEach083, 1));

            return headersObject;
        }
        static string DecodeFormComponent(string value)
            => Uri.UnescapeDataString(value.Replace("+", " "));
        static bool IsFormComponentUnescapedByte(byte value)
            => (value >= (byte)'a' && value <= (byte)'z')
               || (value >= (byte)'A' && value <= (byte)'Z')
               || (value >= (byte)'0' && value <= (byte)'9')
               || value is (byte)'*' or (byte)'-' or (byte)'.' or (byte)'_';
        static string EncodeFormComponent(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            var builder = new StringBuilder(bytes.Length);
            foreach (var current in bytes)
            {
                if (current == (byte)' ')
                {
                    builder.Append('+');
                }
                else if (IsFormComponentUnescapedByte(current))
                {
                    builder.Append((char)current);
                }
                else
                {
                    builder.Append('%');
                    builder.Append(current.ToString("X2"));
                }
            }

            return builder.ToString();
        }
        // A FormData built from a <form> reads that form's entry list through the host, which is what
        // `new FormData(form)` means. It used to enumerate the wrapper's own string properties
        // instead, so it produced the element object's members — tagName, innerHTML and the rest —
        // rather than the form's fields.
        JsValue CreateFormDataObject(JsValue initValue = default)
        {
            var formDataObject = realm.NewObject();
            var entries = new List<KeyValuePair<string, string>>();

            void AppendEntry(string name, string value)
                => entries.Add(new KeyValuePair<string, string>(name, value));

            void SetEntry(string name, string value)
            {
                var firstIndex = -1;
                for (var i = 0; i < entries.Count; i++)
                {
                    if (!string.Equals(entries[i].Key, name, StringComparison.Ordinal))
                        continue;

                    if (firstIndex < 0)
                    {
                        firstIndex = i;
                        entries[i] = new KeyValuePair<string, string>(name, value);
                    }
                    else
                    {
                        entries.RemoveAt(i);
                        i--;
                    }
                }

                if (firstIndex < 0)
                    entries.Add(new KeyValuePair<string, string>(name, value));
            }

            if (!initValue.IsNullish)
            {
                if (initValue.IsObject)
                {
                    if (_host.FormEntriesFor(initValue) is { } formEntries)
                    {
                        foreach (var entry in formEntries)
                            AppendEntry(entry.Key, entry.Value);
                    }
                    else
                    {
                        foreach (var (key, value) in EnumerateObjectStringEntries(initValue))
                            AppendEntry(key, value);
                    }
                }
                else
                {
                    var initText = realm.ToJsString(initValue);
                    if (!string.IsNullOrEmpty(initText))
                    {
                        foreach (var segment in initText.Split('&', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var separatorIndex = segment.IndexOf('=');
                            var rawName = separatorIndex >= 0 ? segment[..separatorIndex] : segment;
                            var rawValue = separatorIndex >= 0 ? segment[(separatorIndex + 1)..] : string.Empty;
                            AppendEntry(DecodeFormComponent(rawName), DecodeFormComponent(rawValue));
                        }
                    }
                }
            }
            JsValue JsRegistrationAppend084(in JsCall call)
            {
                if (call.Length >= 2)
                    AppendEntry(call.Realm.ToJsString(call[0]), call.Realm.ToJsString(call[1]));
                return JsValue.Undefined;
            }

            realm.DefineValue(formDataObject, "append", realm.NewConstructor("append", JsRegistrationAppend084, 2));
            JsValue JsRegistrationDelete085(in JsCall call)
            {
                if (call.Length > 0)
                {
                    var name = call.Realm.ToJsString(call[0]);
                    entries.RemoveAll(entry => string.Equals(entry.Key, name, StringComparison.Ordinal));
                }

                return JsValue.Undefined;
            }
            realm.DefineValue(formDataObject, "delete", realm.NewConstructor("delete", JsRegistrationDelete085, 1));
            JsValue JsRegistrationForEach086(in JsCall call)
            {
                if (call.Length > 0 && call[0].IsFunction)
                {
                    var callback = call[0];
                    foreach (var entry in entries)
                    {
                        realm.Invoke(callback, callback,
                            [JsValue.String(entry.Value), JsValue.String(entry.Key), formDataObject]);
                    }
                }

                return JsValue.Undefined;
            }
            realm.DefineValue(formDataObject, "forEach", realm.NewConstructor("forEach", JsRegistrationForEach086, 1));
            JsValue JsRegistrationGet087(in JsCall call)
            {
                if (call.Length == 0)
                    return JsValue.Null;
                var name = call.Realm.ToJsString(call[0]);
                foreach (var entry in entries)
                {
                    if (string.Equals(entry.Key, name, StringComparison.Ordinal))
                        return JsValue.String(entry.Value);
                }

                return JsValue.Null;
            }
            realm.DefineValue(formDataObject, "get", realm.NewConstructor("get", JsRegistrationGet087, 1));
            JsValue JsRegistrationGetAll088(in JsCall call)
            {
                if (call.Length == 0)
                    return call.Realm.NewArray();
                var name = call.Realm.ToJsString(call[0]);
                var result = new List<JsValue>();
                foreach (var entry in entries)
                {
                    if (string.Equals(entry.Key, name, StringComparison.Ordinal))
                        result.Add(JsValue.String(entry.Value));
                }

                return call.Realm.NewArray([.. result]);
            }
            realm.DefineValue(formDataObject, "getAll", realm.NewConstructor("getAll", JsRegistrationGetAll088, 1));
            JsValue JsRegistrationHas089(in JsCall call)
            {
                if (call.Length == 0)
                    return JsValue.False;
                var name = call.Realm.ToJsString(call[0]);
                return JsValue.Boolean(entries.Any(entry => string.Equals(entry.Key, name, StringComparison.Ordinal)));
            }
            realm.DefineValue(formDataObject, "has", realm.NewConstructor("has", JsRegistrationHas089, 1));
            JsValue JsRegistrationSet090(in JsCall call)
            {
                if (call.Length >= 2)
                    SetEntry(call.Realm.ToJsString(call[0]), call.Realm.ToJsString(call[1]));
                return JsValue.Undefined;
            }
            realm.DefineValue(formDataObject, "set", realm.NewConstructor("set", JsRegistrationSet090, 2));
            realm.DefineValue(
                formDataObject,
                "toString",
                realm.NewConstructor(
                    "toString",
                    (in _) => JsValue.String(string.Join("&", entries.Select(static entry => $"{EncodeFormComponent(entry.Key)}={EncodeFormComponent(entry.Value)}"))),
                    0));

            return formDataObject;
        }
        // A real Blob. This used to build a plain object carrying size/type/text/arrayBuffer and
        // nothing else, so `(await response.blob()) instanceof Blob` was false, `constructor.name`
        // was "Object" and there was no `slice` — a shape-only stub that was invisible only because
        // the interface it was imitating did not exist either.
        JsValue CreateBlobBody(string bodyText, JsValue headersObject) =>
            _host.CreateBlob(
                Encoding.UTF8.GetBytes(bodyText),
                TryGetJsPropertyString(headersObject, "content-type", "Content-Type") ?? string.Empty);
        // "Disturbed or locked", the Body mixin's own test. Disturbed is bodyUsed, which the body
        // stream sets the first time it is read or cancelled; locked is the stream's own answer, so
        // a getReader() that has not read yet still blocks text()/json()/clone() — which is what a
        // browser does and what a page holding a reader expects.
        bool IsBodyUnavailable(JsValue owner)
            => realm.GetProperty(owner, "bodyUsed").AsBoolean
               || _host.IsStreamLocked(realm.GetProperty(owner, "body"));
        // A real ReadableStream over the body's bytes, the same interface a page's own
        // `new ReadableStream` and `blob.stream()` produce. What stood here before was a shape-only
        // object: a getReader whose reader had read/cancel/releaseLock and nothing else — no
        // `closed`, no `tee`, no `cancel` on the stream, and no async iteration, so
        // `for await (const chunk of response.body)` threw on a body that was there.
        JsValue CreateReadableStreamBody(JsValue owner, string bodyText) =>
            // bodyUsed is the Body mixin's "disturbed" flag, and it is the stream being read that
            // sets it — reported from the underlying source, so the stream a page holds is an
            // ordinary one with no own properties of its own.
            _host.StreamOverTextObserved(bodyText, () => realm.SetProperty(owner, "bodyUsed", JsValue.True));

        JsValue CreateRequestObject(JsValue inputValue, JsValue initValue = default)
        {
            string url;
            string method;
            string? body;
            JsValue headersObject;
            var signalValue = JsValue.Undefined;
            string mode = "cors";
            string credentials = "same-origin";
            string cache = "default";
            string redirect = "follow";
            string referrer = "about:client";
            string integrity = string.Empty;

            if (inputValue.IsObject && !string.IsNullOrEmpty(TryGetJsPropertyString(inputValue, "url", "href")))
            {
                url = TryGetJsPropertyString(inputValue, "url", "href") ?? string.Empty;
                method = (TryGetJsPropertyString(inputValue, "method") ?? "GET").ToUpperInvariant();
                body = TryGetJsPropertyString(inputValue, "_bodyInit", "body");
                var inputHeaders = realm.GetProperty(inputValue, "headers");
                headersObject = inputHeaders.IsObject
                    ? CreateHeadersObject(inputHeaders)
                    : CreateHeadersObject();
                var inputSignal = realm.GetProperty(inputValue, "signal");
                // The engine's indexer answered a CLR null for an absent property, which the old
                // `?? JSUndefined.Value` turned into undefined; Missing is that same absence.
                signalValue = inputSignal.IsMissing ? JsValue.Undefined : inputSignal;
                mode = TryGetJsPropertyString(inputValue, "mode") ?? mode;
                credentials = TryGetJsPropertyString(inputValue, "credentials") ?? credentials;
                cache = TryGetJsPropertyString(inputValue, "cache") ?? cache;
                redirect = TryGetJsPropertyString(inputValue, "redirect") ?? redirect;
                referrer = TryGetJsPropertyString(inputValue, "referrer") ?? referrer;
                integrity = TryGetJsPropertyString(inputValue, "integrity") ?? integrity;
            }
            else
            {
                url = realm.ToJsString(inputValue);
                method = "GET";
                body = null;
                headersObject = CreateHeadersObject();
            }

            if (initValue.IsObject)
            {
                method = (TryGetJsPropertyString(initValue, "method") ?? method).ToUpperInvariant();
                if (TryGetJsPropertyString(initValue, "body") is string initBody)
                    body = initBody;
                var initHeaders = realm.GetProperty(initValue, "headers");
                if (initHeaders.IsObject)
                    headersObject = CreateHeadersObject(initHeaders);
                var initSignal = realm.GetProperty(initValue, "signal");
                if (!initSignal.IsNullish)
                    signalValue = initSignal;
                mode = TryGetJsPropertyString(initValue, "mode") ?? mode;
                credentials = TryGetJsPropertyString(initValue, "credentials") ?? credentials;
                cache = TryGetJsPropertyString(initValue, "cache") ?? cache;
                redirect = TryGetJsPropertyString(initValue, "redirect") ?? redirect;
                referrer = TryGetJsPropertyString(initValue, "referrer") ?? referrer;
                integrity = TryGetJsPropertyString(initValue, "integrity") ?? integrity;
            }

            var requestObject = realm.NewObject();
            realm.SetProperty(requestObject, "url", JsValue.String(url));
            realm.SetProperty(requestObject, "method", JsValue.String(method));
            realm.SetProperty(requestObject, "headers", headersObject);
            realm.SetProperty(requestObject, "bodyUsed", JsValue.False);
            realm.SetProperty(requestObject, "_bodyInit", body == null ? JsValue.Null : JsValue.String(body));
            realm.SetProperty(requestObject, "body", body == null ? JsValue.Null : CreateReadableStreamBody(requestObject, body));
            realm.SetProperty(requestObject, "signal", signalValue);
            realm.SetProperty(requestObject, "mode", JsValue.String(mode));
            realm.SetProperty(requestObject, "credentials", JsValue.String(credentials));
            realm.SetProperty(requestObject, "cache", JsValue.String(cache));
            realm.SetProperty(requestObject, "redirect", JsValue.String(redirect));
            realm.SetProperty(requestObject, "referrer", JsValue.String(referrer));
            realm.SetProperty(requestObject, "integrity", JsValue.String(integrity));
            JsValue JsRegistrationClone098(in JsCall call)
            {
                if (IsBodyUnavailable(requestObject))
                    throw call.Realm.Error(JsErrorKind.Error, "Failed to execute 'clone' on 'Request': body is already used.");
                return CreateRequestObject(requestObject);
            }
            realm.DefineValue(requestObject, "clone", realm.NewConstructor("clone", JsRegistrationClone098, 0));
            JsValue JsRegistrationText099(in JsCall call)
            {
                if (IsBodyUnavailable(requestObject))
                    throw call.Realm.Error(JsErrorKind.Error, "Failed to execute body reader on 'Request': body is already used.");
                realm.SetProperty(requestObject, "bodyUsed", JsValue.True);
                return CreateThenable(() => body == null ? JsValue.String(string.Empty) : JsValue.String(body));
            }
            realm.DefineValue(requestObject, "text", realm.NewConstructor("text", JsRegistrationText099, 0));
            JsValue JsRegistrationJson100(in JsCall call)
            {
                if (IsBodyUnavailable(requestObject))
                    throw call.Realm.Error(JsErrorKind.Error, "Failed to execute body reader on 'Request': body is already used.");
                realm.SetProperty(requestObject, "bodyUsed", JsValue.True);
                return CreateThenable(() => ParseJsonText(realm, body ?? string.Empty));
            }
            realm.DefineValue(requestObject, "json", realm.NewConstructor("json", JsRegistrationJson100, 0));
            JsValue JsRegistrationArrayBuffer101(in JsCall call)
            {
                if (IsBodyUnavailable(requestObject))
                    throw call.Realm.Error(JsErrorKind.Error, "Failed to execute body reader on 'Request': body is already used.");
                realm.SetProperty(requestObject, "bodyUsed", JsValue.True);
                return CreateThenable(() => realm.NewArrayBuffer(Encoding.UTF8.GetBytes(body ?? string.Empty)));
            }
            realm.DefineValue(requestObject, "arrayBuffer", realm.NewConstructor("arrayBuffer", JsRegistrationArrayBuffer101, 0));
            JsValue JsRegistrationBlob102(in JsCall call)
            {
                if (IsBodyUnavailable(requestObject))
                    throw call.Realm.Error(JsErrorKind.Error, "Failed to execute body reader on 'Request': body is already used.");
                realm.SetProperty(requestObject, "bodyUsed", JsValue.True);
                return CreateThenable(() => CreateBlobBody(body ?? string.Empty, headersObject));
            }
            realm.DefineValue(requestObject, "blob", realm.NewConstructor("blob", JsRegistrationBlob102, 0));
            JsValue JsRegistrationFormData103(in JsCall call)
            {
                if (IsBodyUnavailable(requestObject))
                    throw call.Realm.Error(JsErrorKind.Error, "Failed to execute body reader on 'Request': body is already used.");
                realm.SetProperty(requestObject, "bodyUsed", JsValue.True);
                return CreateThenable(() => CreateFormDataObject(JsValue.String(body ?? string.Empty)));
            }
            realm.DefineValue(requestObject, "formData", realm.NewConstructor("formData", JsRegistrationFormData103, 0));

            return requestObject;
        }
        JsValue CreateResponse(string body, int statusCode, string statusText, string responseUrl, string type, bool redirected, Dictionary<string, string> headers)
        {
            var responseHeaders = realm.NewObject();
            foreach (var header in headers)
                realm.SetProperty(responseHeaders, header.Key, JsValue.String(header.Value));

            var headersObject = CreateHeadersObject(responseHeaders);
            var responseObject = realm.NewObject();
            realm.SetProperty(responseObject, "ok", JsValue.Boolean(statusCode >= 200 && statusCode < 300));
            realm.SetProperty(responseObject, "status", JsValue.Number(statusCode));
            realm.SetProperty(responseObject, "statusText", JsValue.String(statusText));
            realm.SetProperty(responseObject, "url", JsValue.String(responseUrl));
            realm.SetProperty(responseObject, "redirected", JsValue.Boolean(redirected));
            realm.SetProperty(responseObject, "type", JsValue.String(type));
            realm.SetProperty(responseObject, "bodyUsed", JsValue.False);
            realm.SetProperty(responseObject, "headers", headersObject);
            realm.SetProperty(responseObject, "_bodyText", JsValue.String(body));
            realm.SetProperty(responseObject, "body", CreateReadableStreamBody(responseObject, body));
            JsValue JsRegistrationText104(in JsCall call)
            {
                if (IsBodyUnavailable(responseObject))
                    throw call.Realm.Error(JsErrorKind.Error, "Failed to execute body reader on 'Response': body is already used.");
                realm.SetProperty(responseObject, "bodyUsed", JsValue.True);
                return CreateThenable(() => JsValue.String(body));
            }
            realm.DefineValue(responseObject, "text", realm.NewConstructor("text", JsRegistrationText104, 0));
            JsValue JsRegistrationJson105(in JsCall call)
            {
                if (IsBodyUnavailable(responseObject))
                    throw call.Realm.Error(JsErrorKind.Error, "Failed to execute body reader on 'Response': body is already used.");
                realm.SetProperty(responseObject, "bodyUsed", JsValue.True);
                return CreateThenable(() => ParseResponseJsonText(realm, body));
            }
            realm.DefineValue(responseObject, "json", realm.NewConstructor("json", JsRegistrationJson105, 0));
            JsValue JsRegistrationArrayBuffer106(in JsCall call)
            {
                if (IsBodyUnavailable(responseObject))
                    throw call.Realm.Error(JsErrorKind.Error, "Failed to execute body reader on 'Response': body is already used.");
                realm.SetProperty(responseObject, "bodyUsed", JsValue.True);
                return CreateThenable(() => realm.NewArrayBuffer(Encoding.UTF8.GetBytes(body)));
            }
            realm.DefineValue(responseObject, "arrayBuffer", realm.NewConstructor("arrayBuffer", JsRegistrationArrayBuffer106, 0));
            JsValue JsRegistrationBlob107(in JsCall call)
            {
                if (IsBodyUnavailable(responseObject))
                    throw call.Realm.Error(JsErrorKind.Error, "Failed to execute body reader on 'Response': body is already used.");
                realm.SetProperty(responseObject, "bodyUsed", JsValue.True);
                return CreateThenable(() => CreateBlobBody(body, headersObject));
            }
            realm.DefineValue(responseObject, "blob", realm.NewConstructor("blob", JsRegistrationBlob107, 0));
            JsValue JsRegistrationFormData108(in JsCall call)
            {
                if (IsBodyUnavailable(responseObject))
                    throw call.Realm.Error(JsErrorKind.Error, "Failed to execute body reader on 'Response': body is already used.");
                realm.SetProperty(responseObject, "bodyUsed", JsValue.True);
                return CreateThenable(() => CreateFormDataObject(JsValue.String(body)));
            }
            realm.DefineValue(responseObject, "formData", realm.NewConstructor("formData", JsRegistrationFormData108, 0));
            JsValue JsRegistrationClone109(in JsCall call)
            {
                if (IsBodyUnavailable(responseObject))
                    throw call.Realm.Error(JsErrorKind.Error, "Failed to execute 'clone' on 'Response': body is already used.");
                return CreateResponse(body, statusCode, statusText, responseUrl, type, redirected, new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase));
            }
            realm.DefineValue(responseObject, "clone", realm.NewConstructor("clone", JsRegistrationClone109, 0));

            return responseObject;
        }
        JsValue CreateAbortErrorValue(JsValue signalValue)
        {
            if (signalValue.IsObject)
            {
                var reason = realm.GetProperty(signalValue, "reason");
                if (!reason.IsNullish)
                    return reason;
            }

            var error = realm.NewObject();
            realm.SetProperty(error, "name", JsValue.String("AbortError"));
            realm.SetProperty(error, "message", JsValue.String("The operation was aborted."));
            return error;
        }
        (int status, string statusText, string url, string type, bool redirected, Dictionary<string, string> headers) ParseResponseInit(JsValue initValue)
        {
            var status = 200;
            var statusText = string.Empty;
            var url = string.Empty;
            var type = "basic";
            var redirected = false;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (initValue.IsObject)
            {
                if (TryGetJsPropertyString(initValue, "status") is string statusValue && int.TryParse(statusValue, out var parsedStatus))
                    status = parsedStatus;
                statusText = TryGetJsPropertyString(initValue, "statusText") ?? string.Empty;
                url = TryGetJsPropertyString(initValue, "url") ?? string.Empty;
                type = TryGetJsPropertyString(initValue, "type") ?? "basic";
                redirected = string.Equals(TryGetJsPropertyString(initValue, "redirected"), "true", StringComparison.OrdinalIgnoreCase);

                var initHeaders = realm.GetProperty(initValue, "headers");
                if (initHeaders.IsObject)
                {
                    foreach (var (key, value) in EnumerateObjectStringEntries(initHeaders))
                        headers[key] = value;
                }
            }

            return (status, statusText, url, type, redirected, headers);
        }
        string ResolveResponseRedirectUrl(string redirectUrl)
        {
            if (string.IsNullOrWhiteSpace(redirectUrl))
                throw realm.Error(JsErrorKind.Error, "Failed to execute 'redirect' on 'Response': Invalid URL");

            // fetch adopts the one shared resolver (Phase 7 item 4) — absolute stays, relative resolves
            // against the page URL; an unresolvable URL is the spec's "Invalid URL" TypeError.
            return (UrlResolver.Resolve(redirectUrl, _host.PageUrl)
                    ?? throw realm.Error(JsErrorKind.Error, "Failed to execute 'redirect' on 'Response': Invalid URL"))
                .AbsoluteUri;
        }
        var formDataCtor = realm.NewConstructor("FormData", (in call) => CreateFormDataObject(call[0]), 1);
        var headersCtor = realm.NewConstructor("Headers", (in call) => CreateHeadersObject(call[0]), 1);
        var requestCtor = realm.NewConstructor(
            "Request",
            // The first argument keeps its `undefined` default and the second its "not supplied" one:
            // an input that was never passed is coerced to the string "undefined" for the URL, while
            // an init that was never passed must not be read as an object.
            (in call) => CreateRequestObject(call.Length > 0 ? call[0] : JsValue.Undefined, call[1]),
            2);
        var responseCtor = realm.NewConstructor("Response", (in call) => JsRegistrationResponse113Core(ParseResponseInit, CreateResponse, in call), 2);
        realm.DefineValue(responseCtor, "json", realm.NewConstructor("json", (in call) => JsRegistrationJson114Core(ParseResponseInit, CreateResponse, in call), 2));
        realm.DefineValue(
            responseCtor,
            "error",
            realm.NewConstructor(
                "error",
                (in _) => CreateResponse(string.Empty, 0, string.Empty, string.Empty, "error", false, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
                0));
        realm.DefineValue(responseCtor, "redirect", realm.NewConstructor("redirect", (in call) => JsRegistrationRedirect116Core(ResolveResponseRedirectUrl, CreateResponse, in call), 2));
        realm.DefineValue(window, "FormData", formDataCtor);
        realm.DefineValue(window, "Headers", headersCtor);
        realm.DefineValue(window, "Request", requestCtor);
        realm.DefineValue(window, "Response", responseCtor);
        realm.SetProperty(realm.Global, "FormData", formDataCtor);
        realm.SetProperty(realm.Global, "Headers", headersCtor);
        realm.SetProperty(realm.Global, "Request", requestCtor);
        realm.SetProperty(realm.Global, "Response", responseCtor);
        // fetch(url, options) — polyfill backed by the injected ResourceLoader
        var fetchFn = realm.NewConstructor("fetch", (in call) => JsRegistrationFetch120Core(TryGetJsPropertyString, EnumerateObjectStringEntries, CreateAbortErrorValue, CreateResponse, in call), 1);
        realm.DefineValue(window, "fetch", fetchFn);
        // XMLHttpRequest — basic polyfill backed by fetch/the ResourceLoader
        RegisterXMLHttpRequest(realm);
        return fetchFn;
    }

    /// <summary>The realm's <c>JSON.parse</c> over <paramref name="jsonText"/>, called with no receiver.</summary>
    private JsValue ParseJsonText(IJsRealm realm, string jsonText) =>
        realm.Invoke(_jsonParse, JsValue.Undefined, [JsValue.String(jsonText)]);

    /// <summary>
    /// The same, reporting a malformed body as the message <c>response.json()</c> has always
    /// rejected with rather than as the parser's own.
    /// </summary>
    private JsValue ParseResponseJsonText(IJsRealm realm, string jsonText)
    {
        try
        {
            return ParseJsonText(realm, jsonText);
        }
        catch (Exception ex)
        {
            throw realm.Error(JsErrorKind.Error, $"Failed to parse response body as JSON: {ex.Message}");
        }
    }

    /// <summary>
    /// The realm's <c>JSON.stringify</c>, rendered the way the engine's host-side <c>Stringify</c>
    /// helper this replaced rendered it.
    /// </summary>
    /// <remarks>
    /// The helper wrote a string for every input: <c>undefined</c>, <c>null</c> and a non-finite
    /// number became the literal <c>null</c>, and a function became the empty string. The JavaScript
    /// operation instead answers <c>undefined</c> for the first and the last, so the two arms below
    /// are what keeps <c>Response.json(undefined)</c> a body of <c>"null"</c> — the string it was
    /// before — rather than the string <c>"undefined"</c>.
    /// </remarks>
    private string StringifyJson(IJsRealm realm, JsValue value)
    {
        var stringified = realm.Invoke(_jsonStringify, JsValue.Undefined, [value]);
        if (stringified.IsString)
            return stringified.AsString!;

        return value.IsFunction ? string.Empty : "null";
    }

    /// <summary>
    /// An <c>Error</c> to reject with, for a host failure that carries no JavaScript value of its own.
    /// </summary>
    /// <remarks>
    /// The promise executor used to do this itself — it caught anything the resolver threw and
    /// rejected with an error built from it. <see cref="IJsCalls.Error"/> hands back an exception to
    /// throw rather than a value to reject with, so the value is constructed here through the realm's
    /// own <c>Error</c>, which is what the engine's helper did too.
    /// </remarks>
    private static JsValue ErrorValue(IJsRealm realm, string message)
    {
        var error = realm.GetProperty(realm.Global, "Error");
        return error.IsFunction
            ? realm.Construct(error, [JsValue.String(message)])
            : JsValue.String(message);
    }

}
