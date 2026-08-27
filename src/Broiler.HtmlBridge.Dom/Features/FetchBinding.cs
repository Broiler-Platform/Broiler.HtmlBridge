using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Promise;
using System.Text;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.BuiltIns.Array.Typed;
using Broiler.JavaScript.Storage;
using Broiler.JavaScript.BuiltIns.Array;
using Broiler.JavaScript.BuiltIns.Json;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Engine;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.HtmlBridge.Scripting;
using Broiler.HtmlBridge.Internal.Scripting;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The networking feature binding module (HtmlBridge complexity-reduction roadmap Phase 3, P3.11).
/// It co-locates the whole <c>fetch</c> / <c>XMLHttpRequest</c> surface: the <c>fetch</c> polyfill and
/// its <c>Headers</c>/<c>Request</c>/<c>Response</c>/<c>FormData</c>/<c>Blob</c>/<c>AbortController</c>
/// helper objects, the <c>Response</c> static factories and the <c>XMLHttpRequest</c> polyfill. Host
/// I/O goes through the injected Phase 2 <see cref="ResourceLoader"/> — the "no feature callback
/// constructs an <c>HttpClient</c>" seam Phase 7 builds on — and the only other bridge coupling (the
/// page URL used to resolve <c>Response.redirect</c> relative URLs) is reached through the narrow
/// <see cref="IFetchHost"/> contract. The non-networking registrations that historically lived in this
/// method (<c>MessageChannel</c>, <c>getComputedStyle</c>) were moved back to the window-globals
/// registration site.
/// </summary>
internal sealed partial class FetchBinding(IFetchHost host, ResourceLoader resources)
{
    private readonly IFetchHost _host = host;
    private readonly ResourceLoader _resources = resources;

    private delegate string? JsPropertyStringGetter(JSObject obj, params string[] names);

    private delegate IEnumerable<(string Key, string Value)> ObjectStringEntriesEnumerator(JSObject obj);

    private delegate (int status, string statusText, string url, string type, bool redirected, Dictionary<string, string> headers) ResponseInitParser(JSValue? initValue);

    private delegate JSValue ResponseFactory(string body, int statusCode, string statusText,
        string responseUrl, string type, bool redirected, Dictionary<string, string> headers);

    /// <summary>Installs <c>fetch</c>/<c>Headers</c>/<c>Request</c>/<c>Response</c>/<c>FormData</c> and
    /// <c>XMLHttpRequest</c> on <paramref name="window"/>/<paramref name="context"/>, returning the
    /// <c>fetch</c> function so the caller can register it among the window globals.</summary>
    internal JSFunction Install(JSContext context, JSObject window)
    {
        static IEnumerable<(string Key, string Value)> EnumerateObjectStringEntries(JSObject obj)
        {
            foreach (var (key, value) in obj.Entries)
            {
                if (string.IsNullOrEmpty(key) || key[0] == '_' || value is JSFunction || value.IsUndefined || value.IsNull)
                    continue;

                yield return (key, value.ToString());
            }
        }
        static string? TryGetJsPropertyString(JSObject obj, params string[] names)
        {
            foreach (var name in names)
            {
                var value = obj[(KeyString)name];
                if (value != null && !value.IsUndefined && !value.IsNull)
                    return value.ToString();
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
        /// A real <see cref="JSPromise"/> fixes all of it at once, and the engine's microtask queue is
        /// pumped in a capture (a plain <c>Promise.resolve().then(...)</c> callback runs), so settling
        /// through the real machinery still delivers the callback. The executor constructor also turns a
        /// throwing <paramref name="resolver"/> into a rejection, which is the conforming outcome.
        /// </para>
        /// </remarks>
        static JSObject CreateThenable(Func<JSValue> resolver)
            => new JSPromise((resolve, reject) => resolve(resolver()));
        static JSObject CreateHeadersObject(JSValue? initValue = null)
        {
            var headersObject = new JSObject();
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var originalNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            void SyncHeader(string name)
            {
                if (!values.TryGetValue(name, out var currentValue))
                    currentValue = string.Empty;

                var originalName = originalNames.TryGetValue(name, out var storedName) ? storedName : name;
                headersObject[(KeyString)originalName] = new JSString(currentValue);
                headersObject[(KeyString)name.ToLowerInvariant()] = new JSString(currentValue);
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

            if (initValue is JSObject initObject)
            {
                foreach (var (key, value) in EnumerateObjectStringEntries(initObject))
                    AppendHeader(key, value);
            }
            JSValue JsRegistrationGet078(in Arguments a)
            {
                if (a.Length == 0)
                    return JSNull.Value;
                var name = a[0].ToString();
                return values.TryGetValue(name, out var currentValue) ? new JSString(currentValue) : JSNull.Value;
            }

            headersObject.FastAddValue("get", new JSFunction(JsRegistrationGet078, "get", 1), JSPropertyAttributes.EnumerableConfigurableValue);
            JSValue JsRegistrationHas079(in Arguments a)
            {
                if (a.Length == 0)
                    return JSBoolean.False;
                return values.ContainsKey(a[0].ToString()) ? JSBoolean.True : JSBoolean.False;
            }
            headersObject.FastAddValue("has", new JSFunction(JsRegistrationHas079, "has", 1), JSPropertyAttributes.EnumerableConfigurableValue);
            JSValue JsRegistrationSet080(in Arguments a)
            {
                if (a.Length >= 2)
                    SetHeader(a[0].ToString(), a[1].ToString());
                return JSUndefined.Value;
            }
            headersObject.FastAddValue("set", new JSFunction(JsRegistrationSet080, "set", 2), JSPropertyAttributes.EnumerableConfigurableValue);
            JSValue JsRegistrationAppend081(in Arguments a)
            {
                if (a.Length >= 2)
                    AppendHeader(a[0].ToString(), a[1].ToString());
                return JSUndefined.Value;
            }
            headersObject.FastAddValue("append", new JSFunction(JsRegistrationAppend081, "append", 2), JSPropertyAttributes.EnumerableConfigurableValue);
            JSValue JsRegistrationDelete082(in Arguments a)
            {
                if (a.Length > 0)
                {
                    var name = a[0].ToString();
                    values.Remove(name);
                    originalNames.Remove(name);
                    headersObject[(KeyString)name] = JSUndefined.Value;
                    headersObject[(KeyString)name.ToLowerInvariant()] = JSUndefined.Value;
                }

                return JSUndefined.Value;
            }
            headersObject.FastAddValue("delete", new JSFunction(JsRegistrationDelete082, "delete", 1), JSPropertyAttributes.EnumerableConfigurableValue);
            JSValue JsRegistrationForEach083(in Arguments a)
            {
                if (a.Length > 0 && a[0] is JSFunction cb)
                {
                    foreach (var header in values)
                    {
                        var name = originalNames.TryGetValue(header.Key, out var originalName) ? originalName : header.Key;
                        cb.InvokeFunction(new Arguments(cb, new JSString(header.Value), new JSString(name), headersObject));
                    }
                }

                return JSUndefined.Value;
            }
            headersObject.FastAddValue("forEach", new JSFunction(JsRegistrationForEach083, "forEach", 1), JSPropertyAttributes.EnumerableConfigurableValue);

            return headersObject;
        }
        static JSValue ParseJsonText(string jsonText)
            => JSJSON.Parse(new Arguments(JSUndefined.Value, new JSString(jsonText)));
        static JSValue ParseResponseJsonText(string jsonText)
        {
            try
            {
                return ParseJsonText(jsonText);
            }
            catch (Exception ex)
            {
                throw new JSException($"Failed to parse response body as JSON: {ex.Message}");
            }
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
        // Not static: a FormData built from a <form> reads that form's entry list through the host,
        // which is what `new FormData(form)` means. It used to enumerate the wrapper's own string
        // properties instead, so it produced the element object's members — tagName, innerHTML and
        // the rest — rather than the form's fields.
        JSObject CreateFormDataObject(JSValue? initValue = null)
        {
            var formDataObject = new JSObject();
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

            if (initValue != null && !initValue.IsUndefined && !initValue.IsNull)
            {
                if (initValue is JSObject initObject)
                {
                    if (_host.FormEntriesFor(initObject) is { } formEntries)
                    {
                        foreach (var entry in formEntries)
                            AppendEntry(entry.Key, entry.Value);
                    }
                    else
                    {
                        foreach (var (key, value) in EnumerateObjectStringEntries(initObject))
                            AppendEntry(key, value);
                    }
                }
                else
                {
                    var initText = initValue.ToString();
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
            JSValue JsRegistrationAppend084(in Arguments a)
            {
                if (a.Length >= 2)
                    AppendEntry(a[0].ToString(), a[1].ToString());
                return JSUndefined.Value;
            }

            formDataObject.FastAddValue("append", new JSFunction(JsRegistrationAppend084, "append", 2), JSPropertyAttributes.EnumerableConfigurableValue);
            JSValue JsRegistrationDelete085(in Arguments a)
            {
                if (a.Length > 0)
                {
                    var name = a[0].ToString();
                    entries.RemoveAll(entry => string.Equals(entry.Key, name, StringComparison.Ordinal));
                }

                return JSUndefined.Value;
            }
            formDataObject.FastAddValue("delete", new JSFunction(JsRegistrationDelete085, "delete", 1), JSPropertyAttributes.EnumerableConfigurableValue);
            JSValue JsRegistrationForEach086(in Arguments a)
            {
                if (a.Length > 0 && a[0] is JSFunction cb)
                {
                    foreach (var entry in entries)
                        cb.InvokeFunction(new Arguments(cb, new JSString(entry.Value), new JSString(entry.Key), formDataObject));
                }

                return JSUndefined.Value;
            }
            formDataObject.FastAddValue("forEach", new JSFunction(JsRegistrationForEach086, "forEach", 1), JSPropertyAttributes.EnumerableConfigurableValue);
            JSValue JsRegistrationGet087(in Arguments a)
            {
                if (a.Length == 0)
                    return JSNull.Value;
                var name = a[0].ToString();
                foreach (var entry in entries)
                {
                    if (string.Equals(entry.Key, name, StringComparison.Ordinal))
                        return new JSString(entry.Value);
                }

                return JSNull.Value;
            }
            formDataObject.FastAddValue("get", new JSFunction(JsRegistrationGet087, "get", 1), JSPropertyAttributes.EnumerableConfigurableValue);
            JSValue JsRegistrationGetAll088(in Arguments a)
            {
                var result = new JSArray();
                if (a.Length == 0)
                    return result;
                var name = a[0].ToString();
                foreach (var entry in entries)
                {
                    if (string.Equals(entry.Key, name, StringComparison.Ordinal))
                        result.Add(new JSString(entry.Value));
                }

                return result;
            }
            formDataObject.FastAddValue("getAll", new JSFunction(JsRegistrationGetAll088, "getAll", 1), JSPropertyAttributes.EnumerableConfigurableValue);
            JSValue JsRegistrationHas089(in Arguments a)
            {
                if (a.Length == 0)
                    return JSBoolean.False;
                var name = a[0].ToString();
                return entries.Any(entry => string.Equals(entry.Key, name, StringComparison.Ordinal)) ? JSBoolean.True : JSBoolean.False;
            }
            formDataObject.FastAddValue("has", new JSFunction(JsRegistrationHas089, "has", 1), JSPropertyAttributes.EnumerableConfigurableValue);
            JSValue JsRegistrationSet090(in Arguments a)
            {
                if (a.Length >= 2)
                    SetEntry(a[0].ToString(), a[1].ToString());
                return JSUndefined.Value;
            }
            formDataObject.FastAddValue("set", new JSFunction(JsRegistrationSet090, "set", 2), JSPropertyAttributes.EnumerableConfigurableValue);
            formDataObject.FastAddValue("toString", new JSFunction((in _) => new JSString(string.Join("&", entries.Select(static entry => $"{EncodeFormComponent(entry.Key)}={EncodeFormComponent(entry.Value)}"))),
                "toString", 0), JSPropertyAttributes.EnumerableConfigurableValue);

            return formDataObject;
        }
        // A real Blob. This used to build a plain object carrying size/type/text/arrayBuffer and
        // nothing else, so `(await response.blob()) instanceof Blob` was false, `constructor.name`
        // was "Object" and there was no `slice` — a shape-only stub that was invisible only because
        // the interface it was imitating did not exist either.
        JSValue CreateBlobBody(string bodyText, JSObject headersObject) =>
            _host.CreateBlob(
                Encoding.UTF8.GetBytes(bodyText),
                TryGetJsPropertyString(headersObject, "content-type", "Content-Type") ?? string.Empty);
        // "Disturbed or locked", the Body mixin's own test. Disturbed is bodyUsed, which the body
        // stream sets the first time it is read or cancelled; locked is the stream's own answer, so
        // a getReader() that has not read yet still blocks text()/json()/clone() — which is what a
        // browser does and what a page holding a reader expects.
        bool IsBodyUnavailable(JSObject owner)
            => (owner[(KeyString)"bodyUsed"]?.BooleanValue ?? false)
               || (owner[(KeyString)"body"] is JSObject bodyStream && _host.IsStreamLocked(bodyStream));
        // A real ReadableStream over the body's bytes, the same interface a page's own
        // `new ReadableStream` and `blob.stream()` produce. What stood here before was a shape-only
        // object: a getReader whose reader had read/cancel/releaseLock and nothing else — no
        // `closed`, no `tee`, no `cancel` on the stream, and no async iteration, so
        // `for await (const chunk of response.body)` threw on a body that was there.
        JSValue CreateReadableStreamBody(JSObject owner, string bodyText) =>
            // bodyUsed is the Body mixin's "disturbed" flag, and it is the stream being read that
            // sets it — reported from the underlying source, so the stream a page holds is an
            // ordinary one with no own properties of its own.
            _host.StreamOverTextObserved(bodyText, () => owner[(KeyString)"bodyUsed"] = JSBoolean.True);

        JSObject CreateRequestObject(JSValue inputValue, JSValue? initValue = null)
        {
            string url;
            string method;
            string? body;
            JSObject headersObject;
            JSValue signalValue = JSUndefined.Value;
            string mode = "cors";
            string credentials = "same-origin";
            string cache = "default";
            string redirect = "follow";
            string referrer = "about:client";
            string integrity = string.Empty;

            if (inputValue is JSObject inputObject && !string.IsNullOrEmpty(TryGetJsPropertyString(inputObject, "url", "href")))
            {
                url = TryGetJsPropertyString(inputObject, "url", "href") ?? string.Empty;
                method = (TryGetJsPropertyString(inputObject, "method") ?? "GET").ToUpperInvariant();
                body = TryGetJsPropertyString(inputObject, "_bodyInit", "body");
                headersObject = inputObject[(KeyString)"headers"] is JSObject inputHeaders
                    ? CreateHeadersObject(inputHeaders)
                    : CreateHeadersObject();
                signalValue = inputObject[(KeyString)"signal"] ?? JSUndefined.Value;
                mode = TryGetJsPropertyString(inputObject, "mode") ?? mode;
                credentials = TryGetJsPropertyString(inputObject, "credentials") ?? credentials;
                cache = TryGetJsPropertyString(inputObject, "cache") ?? cache;
                redirect = TryGetJsPropertyString(inputObject, "redirect") ?? redirect;
                referrer = TryGetJsPropertyString(inputObject, "referrer") ?? referrer;
                integrity = TryGetJsPropertyString(inputObject, "integrity") ?? integrity;
            }
            else
            {
                url = inputValue.ToString();
                method = "GET";
                body = null;
                headersObject = CreateHeadersObject();
            }

            if (initValue is JSObject initObject)
            {
                method = (TryGetJsPropertyString(initObject, "method") ?? method).ToUpperInvariant();
                if (TryGetJsPropertyString(initObject, "body") is string initBody)
                    body = initBody;
                if (initObject[(KeyString)"headers"] is JSObject initHeaders)
                    headersObject = CreateHeadersObject(initHeaders);
                if (initObject[(KeyString)"signal"] is { } initSignal && !initSignal.IsUndefined && !initSignal.IsNull)
                    signalValue = initSignal;
                mode = TryGetJsPropertyString(initObject, "mode") ?? mode;
                credentials = TryGetJsPropertyString(initObject, "credentials") ?? credentials;
                cache = TryGetJsPropertyString(initObject, "cache") ?? cache;
                redirect = TryGetJsPropertyString(initObject, "redirect") ?? redirect;
                referrer = TryGetJsPropertyString(initObject, "referrer") ?? referrer;
                integrity = TryGetJsPropertyString(initObject, "integrity") ?? integrity;
            }

            var requestObject = new JSObject();
            requestObject[(KeyString)"url"] = new JSString(url);
            requestObject[(KeyString)"method"] = new JSString(method);
            requestObject[(KeyString)"headers"] = headersObject;
            requestObject[(KeyString)"bodyUsed"] = JSBoolean.False;
            requestObject[(KeyString)"_bodyInit"] = body == null ? JSNull.Value : new JSString(body);
            requestObject[(KeyString)"body"] = body == null ? JSNull.Value : CreateReadableStreamBody(requestObject, body);
            requestObject[(KeyString)"signal"] = signalValue;
            requestObject[(KeyString)"mode"] = new JSString(mode);
            requestObject[(KeyString)"credentials"] = new JSString(credentials);
            requestObject[(KeyString)"cache"] = new JSString(cache);
            requestObject[(KeyString)"redirect"] = new JSString(redirect);
            requestObject[(KeyString)"referrer"] = new JSString(referrer);
            requestObject[(KeyString)"integrity"] = new JSString(integrity);
            JSValue JsRegistrationClone098(in Arguments _)
            {
                if (IsBodyUnavailable(requestObject))
                    throw new JSException("Failed to execute 'clone' on 'Request': body is already used.");
                return CreateRequestObject(requestObject);
            }
            requestObject.FastAddValue("clone", new JSFunction(JsRegistrationClone098, "clone", 0), JSPropertyAttributes.EnumerableConfigurableValue);
            JSValue JsRegistrationText099(in Arguments _)
            {
                if (IsBodyUnavailable(requestObject))
                    throw new JSException("Failed to execute body reader on 'Request': body is already used.");
                requestObject[(KeyString)"bodyUsed"] = JSBoolean.True;
                return CreateThenable(() => body == null ? new JSString(string.Empty) : new JSString(body));
            }
            requestObject.FastAddValue("text", new JSFunction(JsRegistrationText099, "text", 0), JSPropertyAttributes.EnumerableConfigurableValue);
            JSValue JsRegistrationJson100(in Arguments _)
            {
                if (IsBodyUnavailable(requestObject))
                    throw new JSException("Failed to execute body reader on 'Request': body is already used.");
                requestObject[(KeyString)"bodyUsed"] = JSBoolean.True;
                return CreateThenable(() => ParseJsonText(body ?? string.Empty));
            }
            requestObject.FastAddValue("json", new JSFunction(JsRegistrationJson100, "json", 0), JSPropertyAttributes.EnumerableConfigurableValue);
            JSValue JsRegistrationArrayBuffer101(in Arguments _)
            {
                if (IsBodyUnavailable(requestObject))
                    throw new JSException("Failed to execute body reader on 'Request': body is already used.");
                requestObject[(KeyString)"bodyUsed"] = JSBoolean.True;
                return CreateThenable(() => new JSArrayBuffer(Encoding.UTF8.GetBytes(body ?? string.Empty)));
            }
            requestObject.FastAddValue("arrayBuffer", new JSFunction(JsRegistrationArrayBuffer101, "arrayBuffer", 0), JSPropertyAttributes.EnumerableConfigurableValue);
            JSValue JsRegistrationBlob102(in Arguments _)
            {
                if (IsBodyUnavailable(requestObject))
                    throw new JSException("Failed to execute body reader on 'Request': body is already used.");
                requestObject[(KeyString)"bodyUsed"] = JSBoolean.True;
                return CreateThenable(() => CreateBlobBody(body ?? string.Empty, headersObject));
            }
            requestObject.FastAddValue("blob", new JSFunction(JsRegistrationBlob102, "blob", 0), JSPropertyAttributes.EnumerableConfigurableValue);
            JSValue JsRegistrationFormData103(in Arguments _)
            {
                if (IsBodyUnavailable(requestObject))
                    throw new JSException("Failed to execute body reader on 'Request': body is already used.");
                requestObject[(KeyString)"bodyUsed"] = JSBoolean.True;
                return CreateThenable(() => CreateFormDataObject(new JSString(body ?? string.Empty)));
            }
            requestObject.FastAddValue("formData", new JSFunction(JsRegistrationFormData103, "formData", 0), JSPropertyAttributes.EnumerableConfigurableValue);

            return requestObject;
        }
        JSValue CreateResponse(string body, int statusCode, string statusText, string responseUrl, string type, bool redirected, Dictionary<string, string> headers)
        {
            var responseHeaders = new JSObject();
            foreach (var header in headers)
                responseHeaders[(KeyString)header.Key] = new JSString(header.Value);

            var headersObject = CreateHeadersObject(responseHeaders);
            var responseObject = new JSObject();
            responseObject[(KeyString)"ok"] = statusCode >= 200 && statusCode < 300 ? JSBoolean.True : JSBoolean.False;
            responseObject[(KeyString)"status"] = new JSNumber(statusCode);
            responseObject[(KeyString)"statusText"] = new JSString(statusText);
            responseObject[(KeyString)"url"] = new JSString(responseUrl);
            responseObject[(KeyString)"redirected"] = redirected ? JSBoolean.True : JSBoolean.False;
            responseObject[(KeyString)"type"] = new JSString(type);
            responseObject[(KeyString)"bodyUsed"] = JSBoolean.False;
            responseObject[(KeyString)"headers"] = headersObject;
            responseObject[(KeyString)"_bodyText"] = new JSString(body);
            responseObject[(KeyString)"body"] = CreateReadableStreamBody(responseObject, body);
            JSValue JsRegistrationText104(in Arguments _)
            {
                if (IsBodyUnavailable(responseObject))
                    throw new JSException("Failed to execute body reader on 'Response': body is already used.");
                responseObject[(KeyString)"bodyUsed"] = JSBoolean.True;
                return CreateThenable(() => new JSString(body));
            }
            responseObject.FastAddValue("text", new JSFunction(JsRegistrationText104, "text", 0), JSPropertyAttributes.EnumerableConfigurableValue);
            JSValue JsRegistrationJson105(in Arguments _)
            {
                if (IsBodyUnavailable(responseObject))
                    throw new JSException("Failed to execute body reader on 'Response': body is already used.");
                responseObject[(KeyString)"bodyUsed"] = JSBoolean.True;
                return CreateThenable(() => ParseResponseJsonText(body));
            }
            responseObject.FastAddValue("json", new JSFunction(JsRegistrationJson105, "json", 0), JSPropertyAttributes.EnumerableConfigurableValue);
            JSValue JsRegistrationArrayBuffer106(in Arguments _)
            {
                if (IsBodyUnavailable(responseObject))
                    throw new JSException("Failed to execute body reader on 'Response': body is already used.");
                responseObject[(KeyString)"bodyUsed"] = JSBoolean.True;
                return CreateThenable(() => new JSArrayBuffer(Encoding.UTF8.GetBytes(body)));
            }
            responseObject.FastAddValue("arrayBuffer", new JSFunction(JsRegistrationArrayBuffer106, "arrayBuffer", 0), JSPropertyAttributes.EnumerableConfigurableValue);
            JSValue JsRegistrationBlob107(in Arguments _)
            {
                if (IsBodyUnavailable(responseObject))
                    throw new JSException("Failed to execute body reader on 'Response': body is already used.");
                responseObject[(KeyString)"bodyUsed"] = JSBoolean.True;
                return CreateThenable(() => CreateBlobBody(body, headersObject));
            }
            responseObject.FastAddValue("blob", new JSFunction(JsRegistrationBlob107, "blob", 0), JSPropertyAttributes.EnumerableConfigurableValue);
            JSValue JsRegistrationFormData108(in Arguments _)
            {
                if (IsBodyUnavailable(responseObject))
                    throw new JSException("Failed to execute body reader on 'Response': body is already used.");
                responseObject[(KeyString)"bodyUsed"] = JSBoolean.True;
                return CreateThenable(() => CreateFormDataObject(new JSString(body)));
            }
            responseObject.FastAddValue("formData", new JSFunction(JsRegistrationFormData108, "formData", 0), JSPropertyAttributes.EnumerableConfigurableValue);
            JSValue JsRegistrationClone109(in Arguments _)
            {
                if (IsBodyUnavailable(responseObject))
                    throw new JSException("Failed to execute 'clone' on 'Response': body is already used.");
                return CreateResponse(body, statusCode, statusText, responseUrl, type, redirected, new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase));
            }
            responseObject.FastAddValue("clone", new JSFunction(JsRegistrationClone109, "clone", 0), JSPropertyAttributes.EnumerableConfigurableValue);

            return responseObject;
        }
        static JSValue CreateAbortErrorValue(JSValue signalValue)
        {
            if (signalValue is JSObject signalObject)
            {
                var reason = signalObject[(KeyString)"reason"];
                if (reason != null && !reason.IsUndefined && !reason.IsNull)
                    return reason;
            }

            var error = new JSObject();
            error[(KeyString)"name"] = new JSString("AbortError");
            error[(KeyString)"message"] = new JSString("The operation was aborted.");
            return error;
        }
        (int status, string statusText, string url, string type, bool redirected, Dictionary<string, string> headers) ParseResponseInit(JSValue? initValue)
        {
            var status = 200;
            var statusText = string.Empty;
            var url = string.Empty;
            var type = "basic";
            var redirected = false;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (initValue is JSObject initObject)
            {
                if (TryGetJsPropertyString(initObject, "status") is string statusValue && int.TryParse(statusValue, out var parsedStatus))
                    status = parsedStatus;
                statusText = TryGetJsPropertyString(initObject, "statusText") ?? string.Empty;
                url = TryGetJsPropertyString(initObject, "url") ?? string.Empty;
                type = TryGetJsPropertyString(initObject, "type") ?? "basic";
                redirected = string.Equals(TryGetJsPropertyString(initObject, "redirected"), "true", StringComparison.OrdinalIgnoreCase);

                if (initObject[(KeyString)"headers"] is JSObject initHeaders)
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
                throw new JSException("Failed to execute 'redirect' on 'Response': Invalid URL");

            // fetch adopts the one shared resolver (Phase 7 item 4) — absolute stays, relative resolves
            // against the page URL; an unresolvable URL is the spec's "Invalid URL" TypeError.
            return (UrlResolver.Resolve(redirectUrl, _host.PageUrl)
                    ?? throw new JSException("Failed to execute 'redirect' on 'Response': Invalid URL"))
                .AbsoluteUri;
        }
        var formDataCtor = new JSFunction((in a) => CreateFormDataObject(a.Length > 0 ? a[0] : null), "FormData", 1);
        var headersCtor = new JSFunction((in a) => CreateHeadersObject(a.Length > 0 ? a[0] : null), "Headers", 1);
        var requestCtor = new JSFunction((in a) => CreateRequestObject(a.Length > 0 ? a[0] : JSUndefined.Value, a.Length > 1 ? a[1] : null), "Request", 2);
        var responseCtor = new JSFunction((in a) => JsRegistrationResponse113Core(ParseResponseInit, CreateResponse, in a), "Response", 2);
        responseCtor.FastAddValue("json", new JSFunction((in a) => JsRegistrationJson114Core(ParseResponseInit, CreateResponse, in a), "json", 2), JSPropertyAttributes.EnumerableConfigurableValue);
        responseCtor.FastAddValue("error", new JSFunction((in _) => CreateResponse(string.Empty, 0, string.Empty, string.Empty, "error", false, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            "error", 0), JSPropertyAttributes.EnumerableConfigurableValue);
        responseCtor.FastAddValue("redirect", new JSFunction((in a) => JsRegistrationRedirect116Core(ResolveResponseRedirectUrl, CreateResponse, in a), "redirect", 2), JSPropertyAttributes.EnumerableConfigurableValue);
        window.FastAddValue("FormData", formDataCtor, JSPropertyAttributes.EnumerableConfigurableValue);
        window.FastAddValue("Headers", headersCtor, JSPropertyAttributes.EnumerableConfigurableValue);
        window.FastAddValue("Request", requestCtor, JSPropertyAttributes.EnumerableConfigurableValue);
        window.FastAddValue("Response", responseCtor, JSPropertyAttributes.EnumerableConfigurableValue);
        context["FormData"] = formDataCtor;
        context["Headers"] = headersCtor;
        context["Request"] = requestCtor;
        context["Response"] = responseCtor;
        // fetch(url, options) — polyfill backed by the injected ResourceLoader
        var fetchFn = new JSFunction((in a) => JsRegistrationFetch120Core(TryGetJsPropertyString, EnumerateObjectStringEntries, CreateAbortErrorValue, CreateResponse, in a), "fetch", 1);
        window.FastAddValue("fetch", fetchFn, JSPropertyAttributes.EnumerableConfigurableValue);
        // XMLHttpRequest — basic polyfill backed by fetch/the ResourceLoader
        RegisterXMLHttpRequest(context);
        return fetchFn;
    }

}
