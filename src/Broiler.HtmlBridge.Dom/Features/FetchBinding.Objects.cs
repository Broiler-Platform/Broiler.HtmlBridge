using System.Text;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.HtmlBridge.Internal.Scripting;
using Broiler.JSeal;

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

        realm.DefineConstructor(headersObject, "get", 1, JsRegistrationGet078);
        JsValue JsRegistrationHas079(in JsCall call)
        {
            if (call.Length == 0)
                return JsValue.False;
            return JsValue.Boolean(values.ContainsKey(call.Realm.ToJsString(call[0])));
        }
        realm.DefineConstructor(headersObject, "has", 1, JsRegistrationHas079);
        JsValue JsRegistrationSet080(in JsCall call)
        {
            if (call.Length >= 2)
                SetHeader(call.Realm.ToJsString(call[0]), call.Realm.ToJsString(call[1]));
            return JsValue.Undefined;
        }
        realm.DefineConstructor(headersObject, "set", 2, JsRegistrationSet080);
        JsValue JsRegistrationAppend081(in JsCall call)
        {
            if (call.Length >= 2)
                AppendHeader(call.Realm.ToJsString(call[0]), call.Realm.ToJsString(call[1]));
            return JsValue.Undefined;
        }
        realm.DefineConstructor(headersObject, "append", 2, JsRegistrationAppend081);
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
        realm.DefineConstructor(headersObject, "delete", 1, JsRegistrationDelete082);
        JsValue JsRegistrationForEach083(in JsCall call)
        {
            if (call.Length > 0 && call[0].IsFunction)
            {
                var callback = call[0];
                foreach (var header in values)
                {
                    var name = originalNames.TryGetValue(header.Key, out var originalName) ? originalName : header.Key;

                    // The receiver is the callback itself. That is not what the specification says,
                    // but it is what this surface has always done, so correcting it is a behaviour
                    // change rather than a tidy-up.
                    realm.Invoke(callback, callback,
                        [JsValue.String(header.Value), JsValue.String(name), headersObject]);
                }
            }

            return JsValue.Undefined;
        }
        realm.DefineConstructor(headersObject, "forEach", 1, JsRegistrationForEach083);

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

        realm.DefineConstructor(formDataObject, "append", 2, JsRegistrationAppend084);
        JsValue JsRegistrationDelete085(in JsCall call)
        {
            if (call.Length > 0)
            {
                var name = call.Realm.ToJsString(call[0]);
                entries.RemoveAll(entry => string.Equals(entry.Key, name, StringComparison.Ordinal));
            }

            return JsValue.Undefined;
        }
        realm.DefineConstructor(formDataObject, "delete", 1, JsRegistrationDelete085);
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
        realm.DefineConstructor(formDataObject, "forEach", 1, JsRegistrationForEach086);
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
        realm.DefineConstructor(formDataObject, "get", 1, JsRegistrationGet087);
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
        realm.DefineConstructor(formDataObject, "getAll", 1, JsRegistrationGetAll088);
        JsValue JsRegistrationHas089(in JsCall call)
        {
            if (call.Length == 0)
                return JsValue.False;
            var name = call.Realm.ToJsString(call[0]);
            return JsValue.Boolean(entries.Any(entry => string.Equals(entry.Key, name, StringComparison.Ordinal)));
        }
        realm.DefineConstructor(formDataObject, "has", 1, JsRegistrationHas089);
        JsValue JsRegistrationSet090(in JsCall call)
        {
            if (call.Length >= 2)
                SetEntry(call.Realm.ToJsString(call[0]), call.Realm.ToJsString(call[1]));
            return JsValue.Undefined;
        }
        realm.DefineConstructor(formDataObject, "set", 2, JsRegistrationSet090);
        realm.DefineConstructor(
            formDataObject,
            "toString",
            0,
            (in _) => JsValue.String(string.Join("&", entries.Select(static entry => $"{EncodeFormComponent(entry.Key)}={EncodeFormComponent(entry.Value)}"))));

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
        realm.DefineConstructor(requestObject, "clone", 0, JsRegistrationClone098);
        JsValue JsRegistrationText099(in JsCall call)
        {
            if (IsBodyUnavailable(realm, requestObject))
                throw call.Realm.Error(JsErrorKind.Error, "Failed to execute body reader on 'Request': body is already used.");
            realm.SetProperty(requestObject, "bodyUsed", JsValue.True);
            return CreateThenable(realm, () => body == null ? JsValue.String(string.Empty) : JsValue.String(body));
        }
        realm.DefineConstructor(requestObject, "text", 0, JsRegistrationText099);
        JsValue JsRegistrationJson100(in JsCall call)
        {
            if (IsBodyUnavailable(realm, requestObject))
                throw call.Realm.Error(JsErrorKind.Error, "Failed to execute body reader on 'Request': body is already used.");
            realm.SetProperty(requestObject, "bodyUsed", JsValue.True);
            return CreateThenable(realm, () => ParseJsonText(realm, body ?? string.Empty));
        }
        realm.DefineConstructor(requestObject, "json", 0, JsRegistrationJson100);
        JsValue JsRegistrationArrayBuffer101(in JsCall call)
        {
            if (IsBodyUnavailable(realm, requestObject))
                throw call.Realm.Error(JsErrorKind.Error, "Failed to execute body reader on 'Request': body is already used.");
            realm.SetProperty(requestObject, "bodyUsed", JsValue.True);
            return CreateThenable(realm, () => realm.NewArrayBuffer(Encoding.UTF8.GetBytes(body ?? string.Empty)));
        }
        realm.DefineConstructor(requestObject, "arrayBuffer", 0, JsRegistrationArrayBuffer101);
        JsValue JsRegistrationBlob102(in JsCall call)
        {
            if (IsBodyUnavailable(realm, requestObject))
                throw call.Realm.Error(JsErrorKind.Error, "Failed to execute body reader on 'Request': body is already used.");
            realm.SetProperty(requestObject, "bodyUsed", JsValue.True);
            return CreateThenable(realm, () => CreateBlobBody(realm, body ?? string.Empty, headersObject));
        }
        realm.DefineConstructor(requestObject, "blob", 0, JsRegistrationBlob102);
        JsValue JsRegistrationFormData103(in JsCall call)
        {
            if (IsBodyUnavailable(realm, requestObject))
                throw call.Realm.Error(JsErrorKind.Error, "Failed to execute body reader on 'Request': body is already used.");
            realm.SetProperty(requestObject, "bodyUsed", JsValue.True);
            return CreateThenable(realm, () => CreateFormDataObject(realm, JsValue.String(body ?? string.Empty)));
        }
        realm.DefineConstructor(requestObject, "formData", 0, JsRegistrationFormData103);

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
        realm.DefineConstructor(responseObject, "text", 0, JsRegistrationText104);
        JsValue JsRegistrationJson105(in JsCall call)
        {
            if (IsBodyUnavailable(realm, responseObject))
                throw call.Realm.Error(JsErrorKind.Error, "Failed to execute body reader on 'Response': body is already used.");
            realm.SetProperty(responseObject, "bodyUsed", JsValue.True);
            return CreateThenable(realm, () => ParseResponseJsonText(realm, body));
        }
        realm.DefineConstructor(responseObject, "json", 0, JsRegistrationJson105);
        JsValue JsRegistrationArrayBuffer106(in JsCall call)
        {
            if (IsBodyUnavailable(realm, responseObject))
                throw call.Realm.Error(JsErrorKind.Error, "Failed to execute body reader on 'Response': body is already used.");
            realm.SetProperty(responseObject, "bodyUsed", JsValue.True);
            return CreateThenable(realm, () => realm.NewArrayBuffer(Encoding.UTF8.GetBytes(body)));
        }
        realm.DefineConstructor(responseObject, "arrayBuffer", 0, JsRegistrationArrayBuffer106);
        JsValue JsRegistrationBlob107(in JsCall call)
        {
            if (IsBodyUnavailable(realm, responseObject))
                throw call.Realm.Error(JsErrorKind.Error, "Failed to execute body reader on 'Response': body is already used.");
            realm.SetProperty(responseObject, "bodyUsed", JsValue.True);
            return CreateThenable(realm, () => CreateBlobBody(realm, body, headersObject));
        }
        realm.DefineConstructor(responseObject, "blob", 0, JsRegistrationBlob107);
        JsValue JsRegistrationFormData108(in JsCall call)
        {
            if (IsBodyUnavailable(realm, responseObject))
                throw call.Realm.Error(JsErrorKind.Error, "Failed to execute body reader on 'Response': body is already used.");
            realm.SetProperty(responseObject, "bodyUsed", JsValue.True);
            return CreateThenable(realm, () => CreateFormDataObject(realm, JsValue.String(body)));
        }
        realm.DefineConstructor(responseObject, "formData", 0, JsRegistrationFormData108);
        JsValue JsRegistrationClone109(in JsCall call)
        {
            if (IsBodyUnavailable(realm, responseObject))
                throw call.Realm.Error(JsErrorKind.Error, "Failed to execute 'clone' on 'Response': body is already used.");
            return CreateResponse(realm, body, statusCode, statusText, responseUrl, type, redirected, new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase));
        }
        realm.DefineConstructor(responseObject, "clone", 0, JsRegistrationClone109);

        return responseObject;
    }
}
