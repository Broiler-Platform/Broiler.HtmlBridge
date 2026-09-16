using System.Text;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.HtmlBridge.Internal.Scripting;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The objects <c>fetch</c> hands a page and takes from it: <c>Headers</c>, <c>FormData</c>,
/// <c>Request</c> and <c>Response</c>, and the <c>Blob</c> and <c>ReadableStream</c> a body is read as.
/// Each factory takes the realm <see cref="Install"/> was given.
/// </summary>
internal sealed partial class FetchBinding
{
    private JsValue CreateHeadersObject(IJsRealm realm, JsValue initValue = default)
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
            foreach (var (key, value) in EnumerateObjectStringEntries(realm, initValue))
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

    private static string DecodeFormComponent(string value)
        => Uri.UnescapeDataString(value.Replace("+", " "));

    private static bool IsFormComponentUnescapedByte(byte value)
        => (value >= (byte)'a' && value <= (byte)'z')
           || (value >= (byte)'A' && value <= (byte)'Z')
           || (value >= (byte)'0' && value <= (byte)'9')
           || value is (byte)'*' or (byte)'-' or (byte)'.' or (byte)'_';

    private static string EncodeFormComponent(string value)
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
    private JsValue CreateFormDataObject(IJsRealm realm, JsValue initValue = default)
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
                    foreach (var (key, value) in EnumerateObjectStringEntries(realm, initValue))
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
    private JsValue CreateBlobBody(IJsRealm realm, string bodyText, JsValue headersObject) =>
        _host.CreateBlob(
            Encoding.UTF8.GetBytes(bodyText),
            TryGetJsPropertyString(realm, headersObject, "content-type", "Content-Type") ?? string.Empty);

    // "Disturbed or locked", the Body mixin's own test. Disturbed is bodyUsed, which the body
    // stream sets the first time it is read or cancelled; locked is the stream's own answer, so
    // a getReader() that has not read yet still blocks text()/json()/clone() — which is what a
    // browser does and what a page holding a reader expects.
    private bool IsBodyUnavailable(IJsRealm realm, JsValue owner)
        => realm.GetProperty(owner, "bodyUsed").AsBoolean
           || _host.IsStreamLocked(realm.GetProperty(owner, "body"));

    // A real ReadableStream over the body's bytes, the same interface a page's own
    // `new ReadableStream` and `blob.stream()` produce. What stood here before was a shape-only
    // object: a getReader whose reader had read/cancel/releaseLock and nothing else — no
    // `closed`, no `tee`, no `cancel` on the stream, and no async iteration, so
    // `for await (const chunk of response.body)` threw on a body that was there.
    private JsValue CreateReadableStreamBody(IJsRealm realm, JsValue owner, string bodyText) =>
        // bodyUsed is the Body mixin's "disturbed" flag, and it is the stream being read that
        // sets it — reported from the underlying source, so the stream a page holds is an
        // ordinary one with no own properties of its own.
        _host.StreamOverTextObserved(bodyText, () => realm.SetProperty(owner, "bodyUsed", JsValue.True));

    private JsValue CreateRequestObject(IJsRealm realm, JsValue inputValue, JsValue initValue = default)
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

        if (inputValue.IsObject && !string.IsNullOrEmpty(TryGetJsPropertyString(realm, inputValue, "url", "href")))
        {
            url = TryGetJsPropertyString(realm, inputValue, "url", "href") ?? string.Empty;
            method = (TryGetJsPropertyString(realm, inputValue, "method") ?? "GET").ToUpperInvariant();
            body = TryGetJsPropertyString(realm, inputValue, "_bodyInit", "body");
            var inputHeaders = realm.GetProperty(inputValue, "headers");
            headersObject = inputHeaders.IsObject
                ? CreateHeadersObject(realm, inputHeaders)
                : CreateHeadersObject(realm);
            var inputSignal = realm.GetProperty(inputValue, "signal");
            // The engine's indexer answered a CLR null for an absent property, which the old
            // `?? JSUndefined.Value` turned into undefined; Missing is that same absence.
            signalValue = inputSignal.IsMissing ? JsValue.Undefined : inputSignal;
            mode = TryGetJsPropertyString(realm, inputValue, "mode") ?? mode;
            credentials = TryGetJsPropertyString(realm, inputValue, "credentials") ?? credentials;
            cache = TryGetJsPropertyString(realm, inputValue, "cache") ?? cache;
            redirect = TryGetJsPropertyString(realm, inputValue, "redirect") ?? redirect;
            referrer = TryGetJsPropertyString(realm, inputValue, "referrer") ?? referrer;
            integrity = TryGetJsPropertyString(realm, inputValue, "integrity") ?? integrity;
        }
        else
        {
            url = realm.ToJsString(inputValue);
            method = "GET";
            body = null;
            headersObject = CreateHeadersObject(realm);
        }

        if (initValue.IsObject)
        {
            method = (TryGetJsPropertyString(realm, initValue, "method") ?? method).ToUpperInvariant();
            if (TryGetJsPropertyString(realm, initValue, "body") is string initBody)
                body = initBody;
            var initHeaders = realm.GetProperty(initValue, "headers");
            if (initHeaders.IsObject)
                headersObject = CreateHeadersObject(realm, initHeaders);
            var initSignal = realm.GetProperty(initValue, "signal");
            if (!initSignal.IsNullish)
                signalValue = initSignal;
            mode = TryGetJsPropertyString(realm, initValue, "mode") ?? mode;
            credentials = TryGetJsPropertyString(realm, initValue, "credentials") ?? credentials;
            cache = TryGetJsPropertyString(realm, initValue, "cache") ?? cache;
            redirect = TryGetJsPropertyString(realm, initValue, "redirect") ?? redirect;
            referrer = TryGetJsPropertyString(realm, initValue, "referrer") ?? referrer;
            integrity = TryGetJsPropertyString(realm, initValue, "integrity") ?? integrity;
        }

        var requestObject = realm.NewObject();
        realm.SetProperty(requestObject, "url", JsValue.String(url));
        realm.SetProperty(requestObject, "method", JsValue.String(method));
        realm.SetProperty(requestObject, "headers", headersObject);
        realm.SetProperty(requestObject, "bodyUsed", JsValue.False);
        realm.SetProperty(requestObject, "_bodyInit", body == null ? JsValue.Null : JsValue.String(body));
        realm.SetProperty(requestObject, "body", body == null ? JsValue.Null : CreateReadableStreamBody(realm, requestObject, body));
        realm.SetProperty(requestObject, "signal", signalValue);
        realm.SetProperty(requestObject, "mode", JsValue.String(mode));
        realm.SetProperty(requestObject, "credentials", JsValue.String(credentials));
        realm.SetProperty(requestObject, "cache", JsValue.String(cache));
        realm.SetProperty(requestObject, "redirect", JsValue.String(redirect));
        realm.SetProperty(requestObject, "referrer", JsValue.String(referrer));
        realm.SetProperty(requestObject, "integrity", JsValue.String(integrity));
        JsValue JsRegistrationClone098(in JsCall call)
        {
            if (IsBodyUnavailable(realm, requestObject))
                throw call.Realm.Error(JsErrorKind.Error, "Failed to execute 'clone' on 'Request': body is already used.");
            return CreateRequestObject(realm, requestObject);
        }
        realm.DefineValue(requestObject, "clone", realm.NewConstructor("clone", JsRegistrationClone098, 0));
        JsValue JsRegistrationText099(in JsCall call)
        {
            if (IsBodyUnavailable(realm, requestObject))
                throw call.Realm.Error(JsErrorKind.Error, "Failed to execute body reader on 'Request': body is already used.");
            realm.SetProperty(requestObject, "bodyUsed", JsValue.True);
            return CreateThenable(realm, () => body == null ? JsValue.String(string.Empty) : JsValue.String(body));
        }
        realm.DefineValue(requestObject, "text", realm.NewConstructor("text", JsRegistrationText099, 0));
        JsValue JsRegistrationJson100(in JsCall call)
        {
            if (IsBodyUnavailable(realm, requestObject))
                throw call.Realm.Error(JsErrorKind.Error, "Failed to execute body reader on 'Request': body is already used.");
            realm.SetProperty(requestObject, "bodyUsed", JsValue.True);
            return CreateThenable(realm, () => ParseJsonText(realm, body ?? string.Empty));
        }
        realm.DefineValue(requestObject, "json", realm.NewConstructor("json", JsRegistrationJson100, 0));
        JsValue JsRegistrationArrayBuffer101(in JsCall call)
        {
            if (IsBodyUnavailable(realm, requestObject))
                throw call.Realm.Error(JsErrorKind.Error, "Failed to execute body reader on 'Request': body is already used.");
            realm.SetProperty(requestObject, "bodyUsed", JsValue.True);
            return CreateThenable(realm, () => realm.NewArrayBuffer(Encoding.UTF8.GetBytes(body ?? string.Empty)));
        }
        realm.DefineValue(requestObject, "arrayBuffer", realm.NewConstructor("arrayBuffer", JsRegistrationArrayBuffer101, 0));
        JsValue JsRegistrationBlob102(in JsCall call)
        {
            if (IsBodyUnavailable(realm, requestObject))
                throw call.Realm.Error(JsErrorKind.Error, "Failed to execute body reader on 'Request': body is already used.");
            realm.SetProperty(requestObject, "bodyUsed", JsValue.True);
            return CreateThenable(realm, () => CreateBlobBody(realm, body ?? string.Empty, headersObject));
        }
        realm.DefineValue(requestObject, "blob", realm.NewConstructor("blob", JsRegistrationBlob102, 0));
        JsValue JsRegistrationFormData103(in JsCall call)
        {
            if (IsBodyUnavailable(realm, requestObject))
                throw call.Realm.Error(JsErrorKind.Error, "Failed to execute body reader on 'Request': body is already used.");
            realm.SetProperty(requestObject, "bodyUsed", JsValue.True);
            return CreateThenable(realm, () => CreateFormDataObject(realm, JsValue.String(body ?? string.Empty)));
        }
        realm.DefineValue(requestObject, "formData", realm.NewConstructor("formData", JsRegistrationFormData103, 0));

        return requestObject;
    }

    private JsValue CreateResponse(IJsRealm realm, string body, int statusCode, string statusText, string responseUrl, string type, bool redirected, Dictionary<string, string> headers)
    {
        var responseHeaders = realm.NewObject();
        foreach (var header in headers)
            realm.SetProperty(responseHeaders, header.Key, JsValue.String(header.Value));

        var headersObject = CreateHeadersObject(realm, responseHeaders);
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
        realm.SetProperty(responseObject, "body", CreateReadableStreamBody(realm, responseObject, body));
        JsValue JsRegistrationText104(in JsCall call)
        {
            if (IsBodyUnavailable(realm, responseObject))
                throw call.Realm.Error(JsErrorKind.Error, "Failed to execute body reader on 'Response': body is already used.");
            realm.SetProperty(responseObject, "bodyUsed", JsValue.True);
            return CreateThenable(realm, () => JsValue.String(body));
        }
        realm.DefineValue(responseObject, "text", realm.NewConstructor("text", JsRegistrationText104, 0));
        JsValue JsRegistrationJson105(in JsCall call)
        {
            if (IsBodyUnavailable(realm, responseObject))
                throw call.Realm.Error(JsErrorKind.Error, "Failed to execute body reader on 'Response': body is already used.");
            realm.SetProperty(responseObject, "bodyUsed", JsValue.True);
            return CreateThenable(realm, () => ParseResponseJsonText(realm, body));
        }
        realm.DefineValue(responseObject, "json", realm.NewConstructor("json", JsRegistrationJson105, 0));
        JsValue JsRegistrationArrayBuffer106(in JsCall call)
        {
            if (IsBodyUnavailable(realm, responseObject))
                throw call.Realm.Error(JsErrorKind.Error, "Failed to execute body reader on 'Response': body is already used.");
            realm.SetProperty(responseObject, "bodyUsed", JsValue.True);
            return CreateThenable(realm, () => realm.NewArrayBuffer(Encoding.UTF8.GetBytes(body)));
        }
        realm.DefineValue(responseObject, "arrayBuffer", realm.NewConstructor("arrayBuffer", JsRegistrationArrayBuffer106, 0));
        JsValue JsRegistrationBlob107(in JsCall call)
        {
            if (IsBodyUnavailable(realm, responseObject))
                throw call.Realm.Error(JsErrorKind.Error, "Failed to execute body reader on 'Response': body is already used.");
            realm.SetProperty(responseObject, "bodyUsed", JsValue.True);
            return CreateThenable(realm, () => CreateBlobBody(realm, body, headersObject));
        }
        realm.DefineValue(responseObject, "blob", realm.NewConstructor("blob", JsRegistrationBlob107, 0));
        JsValue JsRegistrationFormData108(in JsCall call)
        {
            if (IsBodyUnavailable(realm, responseObject))
                throw call.Realm.Error(JsErrorKind.Error, "Failed to execute body reader on 'Response': body is already used.");
            realm.SetProperty(responseObject, "bodyUsed", JsValue.True);
            return CreateThenable(realm, () => CreateFormDataObject(realm, JsValue.String(body)));
        }
        realm.DefineValue(responseObject, "formData", realm.NewConstructor("formData", JsRegistrationFormData108, 0));
        JsValue JsRegistrationClone109(in JsCall call)
        {
            if (IsBodyUnavailable(realm, responseObject))
                throw call.Realm.Error(JsErrorKind.Error, "Failed to execute 'clone' on 'Response': body is already used.");
            return CreateResponse(realm, body, statusCode, statusText, responseUrl, type, redirected, new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase));
        }
        realm.DefineValue(responseObject, "clone", realm.NewConstructor("clone", JsRegistrationClone109, 0));

        return responseObject;
    }
}
