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

        realm.DefineMethod(headersObject, "get", 1, JsRegistrationGet078);
        JsValue JsRegistrationHas079(in JsCall call)
        {
            if (call.Length == 0)
                return JsValue.False;
            return JsValue.Boolean(values.ContainsKey(call.Realm.ToJsString(call[0])));
        }
        realm.DefineMethod(headersObject, "has", 1, JsRegistrationHas079);
        JsValue JsRegistrationSet080(in JsCall call)
        {
            if (call.Length >= 2)
                SetHeader(call.Realm.ToJsString(call[0]), call.Realm.ToJsString(call[1]));
            return JsValue.Undefined;
        }
        realm.DefineMethod(headersObject, "set", 2, JsRegistrationSet080);
        JsValue JsRegistrationAppend081(in JsCall call)
        {
            if (call.Length >= 2)
                AppendHeader(call.Realm.ToJsString(call[0]), call.Realm.ToJsString(call[1]));
            return JsValue.Undefined;
        }
        realm.DefineMethod(headersObject, "append", 2, JsRegistrationAppend081);
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
        realm.DefineMethod(headersObject, "delete", 1, JsRegistrationDelete082);
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
        realm.DefineMethod(headersObject, "forEach", 1, JsRegistrationForEach083);

        return headersObject;
    }

    private static string DecodeFormComponent(string value)
        => Uri.UnescapeDataString(value.Replace("+", " "));

    private static bool IsFormComponentUnescapedByte(byte value)
        => (value >= (byte)'a' && value <= (byte)'z')
           || (value >= (byte)'A' && value <= (byte)'Z')
           || (value >= (byte)'0' && value <= (byte)'9')
           || value is (byte)'*' or (byte)'-' or (byte)'.' or (byte)'_';

    internal static string EncodeFormComponent(string value)
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

    // Makes a FormData iterable; installed with the fetch surface, so it belongs to the realm in use.
    private JsValue _formDataIterable;

    /// <summary>
    /// A <c>FormData</c> holding <paramref name="entries"/>, reporting every <c>append</c>, <c>set</c> and
    /// <c>delete</c> made to it through <paramref name="onEdit"/> -- what a form's submission hands its
    /// <c>formdata</c> listeners, so what they change can reach the host's entry list.
    /// </summary>
    internal JsValue CreateFormData(IJsRealm realm, IEnumerable<FormEntry> entries, Action<FormDataEdit>? onEdit) =>
        CreateFormDataObject(realm, default,
            entries.Select(entry => new KeyValuePair<string, JsValue>(entry.Name,
                !entry.IsFile ? JsValue.String(entry.Value)
                : entry.FileObject.IsObject ? entry.FileObject
                : _host.EmptyEntryFile(realm))),
            onEdit);

    // A FormData built from a <form> reads that form's entry list through the host, which is what
    // `new FormData(form)` means. It used to enumerate the wrapper's own string properties
    // instead, so it produced the element object's members — tagName, innerHTML and the rest —
    // rather than the form's fields.
    // Each entry's value is a string or a file (a blob a page appended, or a file input's), as a browser's is.
    private JsValue CreateFormDataObject(IJsRealm realm, JsValue initValue = default,
        IEnumerable<KeyValuePair<string, JsValue>>? initialEntries = null, Action<FormDataEdit>? onEdit = null)
    {
        var formDataObject = realm.NewObject();
        var entries = new List<KeyValuePair<string, JsValue>>(initialEntries ?? []);

        // What a FormData body is made of: the live list, so what the page appends later is sent.
        _formDataEntries.AddOrUpdate(IdentityOf(formDataObject), entries);

        void AppendEntry(string name, JsValue value)
            => entries.Add(new KeyValuePair<string, JsValue>(name, value));

        // What a host replaying a listener's change submits for the value: a string, or a file's name,
        // which is what a URL-encoded submission sends for a file.
        string EditValue(JsValue value) => value.IsString ? value.AsString! : _host.FileNameOf(value) ?? string.Empty;

        JsValue EntryValue(in JsCall call) =>
            _host.FormDataEntryValue(call.Realm, call[1], call.Length > 2 && !call[2].IsUndefined ? call.Realm.ToJsString(call[2]) : null);

        void SetEntry(string name, JsValue value)
        {
            var firstIndex = -1;
            for (var i = 0; i < entries.Count; i++)
            {
                if (!string.Equals(entries[i].Key, name, StringComparison.Ordinal))
                    continue;

                if (firstIndex < 0)
                {
                    firstIndex = i;
                    entries[i] = new KeyValuePair<string, JsValue>(name, value);
                }
                else
                {
                    entries.RemoveAt(i);
                    i--;
                }
            }

            if (firstIndex < 0)
                entries.Add(new KeyValuePair<string, JsValue>(name, value));
        }

        if (!initValue.IsNullish)
        {
            if (initValue.IsObject)
            {
                foreach (var (key, value) in EnumerateObjectStringEntries(realm, initValue))
                    AppendEntry(key, JsValue.String(value));
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
                        AppendEntry(DecodeFormComponent(rawName), JsValue.String(DecodeFormComponent(rawValue)));
                    }
                }
            }
        }
        JsValue JsRegistrationAppend084(in JsCall call)
        {
            if (call.Length >= 2)
            {
                var (name, value) = (call.Realm.ToJsString(call[0]), EntryValue(in call));
                AppendEntry(name, value);
                onEdit?.Invoke(new FormDataEdit(FormDataEditKind.Append, name, EditValue(value)));
            }

            return JsValue.Undefined;
        }

        realm.DefineMethod(formDataObject, "append", 2, JsRegistrationAppend084);
        JsValue JsRegistrationDelete085(in JsCall call)
        {
            if (call.Length > 0)
            {
                var name = call.Realm.ToJsString(call[0]);
                entries.RemoveAll(entry => string.Equals(entry.Key, name, StringComparison.Ordinal));
                onEdit?.Invoke(new FormDataEdit(FormDataEditKind.Delete, name));
            }

            return JsValue.Undefined;
        }
        realm.DefineMethod(formDataObject, "delete", 1, JsRegistrationDelete085);
        JsValue JsRegistrationForEach086(in JsCall call)
        {
            if (call.Length > 0 && call[0].IsFunction)
            {
                var callback = call[0];
                foreach (var entry in entries)
                {
                    realm.Invoke(callback, callback,
                        [entry.Value, JsValue.String(entry.Key), formDataObject]);
                }
            }

            return JsValue.Undefined;
        }
        realm.DefineMethod(formDataObject, "forEach", 1, JsRegistrationForEach086);
        JsValue JsRegistrationGet087(in JsCall call)
        {
            if (call.Length == 0)
                return JsValue.Null;
            var name = call.Realm.ToJsString(call[0]);
            foreach (var entry in entries)
            {
                if (string.Equals(entry.Key, name, StringComparison.Ordinal))
                    return entry.Value;
            }

            return JsValue.Null;
        }
        realm.DefineMethod(formDataObject, "get", 1, JsRegistrationGet087);
        JsValue JsRegistrationGetAll088(in JsCall call)
        {
            if (call.Length == 0)
                return call.Realm.NewArray();
            var name = call.Realm.ToJsString(call[0]);
            var result = new List<JsValue>();
            foreach (var entry in entries)
            {
                if (string.Equals(entry.Key, name, StringComparison.Ordinal))
                    result.Add(entry.Value);
            }

            return call.Realm.NewArray([.. result]);
        }
        realm.DefineMethod(formDataObject, "getAll", 1, JsRegistrationGetAll088);
        JsValue JsRegistrationHas089(in JsCall call)
        {
            if (call.Length == 0)
                return JsValue.False;
            var name = call.Realm.ToJsString(call[0]);
            return JsValue.Boolean(entries.Any(entry => string.Equals(entry.Key, name, StringComparison.Ordinal)));
        }
        realm.DefineMethod(formDataObject, "has", 1, JsRegistrationHas089);
        JsValue JsRegistrationSet090(in JsCall call)
        {
            if (call.Length >= 2)
            {
                var (name, value) = (call.Realm.ToJsString(call[0]), EntryValue(in call));
                SetEntry(name, value);
                onEdit?.Invoke(new FormDataEdit(FormDataEditKind.Set, name, EditValue(value)));
            }

            return JsValue.Undefined;
        }
        realm.DefineMethod(formDataObject, "set", 2, JsRegistrationSet090);

        // entries(), keys(), values() and the iterator: what `for (const [name, value] of formData)`,
        // Object.fromEntries(formData) and new URLSearchParams(formData) read. They did not exist, so
        // each of those threw or saw nothing.
        JsValue Iterate(IJsRealm callRealm, Func<KeyValuePair<string, JsValue>, JsValue> select)
        {
            var array = callRealm.NewArray([.. entries.Select(select)]);
            return callRealm.Invoke(callRealm.GetProperty(array, "values"), array);
        }

        realm.DefineMethod(formDataObject, "entries", 0, (in call) =>
        {
            var callRealm = call.Realm;
            return Iterate(callRealm, entry => callRealm.NewArray([JsValue.String(entry.Key), entry.Value]));
        });
        realm.DefineMethod(formDataObject, "keys", 0, (in call) => Iterate(call.Realm, static entry => JsValue.String(entry.Key)));
        realm.DefineMethod(formDataObject, "values", 0, (in call) => Iterate(call.Realm, static entry => entry.Value));
        if (_formDataIterable.IsFunction)
            realm.Invoke(_formDataIterable, JsValue.Undefined, [formDataObject]);
        realm.DefineMethod(
            formDataObject,
            "toString",
            0,
            (in _) => JsValue.String(string.Join("&", entries.Select(entry => $"{EncodeFormComponent(entry.Key)}={EncodeFormComponent(EditValue(entry.Value))}"))));

        return formDataObject;
    }

    // A real Blob. This used to build a plain object carrying size/type/text/arrayBuffer and
    // nothing else, so `(await response.blob()) instanceof Blob` was false, `constructor.name`
    // was "Object" and there was no `slice` — a shape-only stub that was invisible only because
    // the interface it was imitating did not exist either.
    //
    // Its type is the Content-Type's MIME type without parameters (measured: a
    // `text/plain; charset=iso-8859-1` response's blob is `text/plain`).
    private JsValue CreateBlobBody(IJsRealm realm, byte[] body, JsValue headersObject) =>
        _host.CreateBlob(body, EssenceOf(TryGetJsPropertyString(realm, headersObject, "content-type", "Content-Type")));

    // "Disturbed or locked", the Body mixin's own test. Disturbed is bodyUsed, which the body
    // stream sets the first time it is read or cancelled; locked is the stream's own answer, so
    // a getReader() that has not read yet still blocks text()/json()/clone() — which is what a
    // browser does and what a page holding a reader expects.
    private bool IsBodyUnavailable(IJsRealm realm, JsValue owner)
        => realm.GetProperty(owner, "bodyUsed").AsBoolean
           || _host.IsStreamLocked(realm.GetProperty(owner, "body"));

    // Every reader the Body mixin gives Request and Response is the same three steps: refuse a body
    // that is already disturbed or locked, mark it disturbed, then hand back a thenable over the
    // decode. text/json/arrayBuffer/blob/formData differ in nothing but `read` — how the bytes
    // become a value — and the interface name the refusal quotes. The guard asks the realm the
    // object lives in, while the error is minted in the calling realm, because that is the realm
    // about to catch it.
    private void DefineBodyReader(IJsRealm realm, JsValue owner, string ownerName, string member, Func<JsValue> read)
    {
        JsValue JsRegistrationBodyReader(in JsCall call)
        {
            if (IsBodyUnavailable(realm, owner))
                throw call.Realm.Error(JsErrorKind.Error, $"Failed to execute body reader on '{ownerName}': body is already used.");
            MarkBodyDisturbed(realm, owner);
            return CreateThenable(realm, read);
        }

        realm.DefineMethod(owner, member, 0, JsRegistrationBodyReader);
    }

    // The body is being read: bodyUsed, and whatever waited for the read (ReleaseOnBodyRead).
    private void MarkBodyDisturbed(IJsRealm realm, JsValue owner)
    {
        realm.SetProperty(owner, "bodyUsed", JsValue.True);
        if (_bodyReadCallbacks.TryGetValue(IdentityOf(owner), out var onRead))
        {
            _bodyReadCallbacks.Remove(IdentityOf(owner));
            onRead();
        }
    }

    // Runs onRead when the body of the response a fetch() resolved with is first read, by a body
    // reader or through its stream -- or a clone's is.
    private void ReleaseOnBodyRead(JsValue response, Action onRead) =>
        _bodyReadCallbacks.AddOrUpdate(IdentityOf(response), onRead);

    // A real ReadableStream over the body's bytes, the same interface a page's own
    // `new ReadableStream` and `blob.stream()` produce. What stood here before was a shape-only
    // object: a getReader whose reader had read/cancel/releaseLock and nothing else — no
    // `closed`, no `tee`, no `cancel` on the stream, and no async iteration, so
    // `for await (const chunk of response.body)` threw on a body that was there.
    private JsValue CreateReadableStreamBody(IJsRealm realm, JsValue owner, byte[] body) =>
        // bodyUsed is the Body mixin's "disturbed" flag, and it is the stream being read that
        // sets it — reported from the underlying source, so the stream a page holds is an
        // ordinary one with no own properties of its own.
        _host.StreamOverBytesObserved(body, () => MarkBodyDisturbed(realm, owner));

    private JsValue CreateRequestObject(IJsRealm realm, JsValue inputValue, JsValue initValue = default)
    {
        string url;
        BodyWithType? body;
        JsValue headersObject;
        var signalValue = JsValue.Undefined;
        string? inputMode = null, inputCredentials = null, inputRedirect = null;
        string? initMode = null, initCredentials = null, initRedirect = null;
        string? methodName = null;
        string cache = "default";
        string referrer = "about:client";
        string integrity = string.Empty;

        if (inputValue.IsObject && !string.IsNullOrEmpty(TryGetJsPropertyString(realm, inputValue, "url", "href")))
        {
            url = TryGetJsPropertyString(realm, inputValue, "url", "href") ?? string.Empty;
            methodName = TryGetJsPropertyString(realm, inputValue, "method");
            // A Request's body is the one it was made with; its headers already carry its type.
            body = RequestBodyOf(inputValue) is { } stored ? new BodyWithType(stored.Bytes, null) : null;
            var inputHeaders = realm.GetProperty(inputValue, "headers");
            headersObject = inputHeaders.IsObject
                ? CreateHeadersObject(realm, inputHeaders)
                : CreateHeadersObject(realm);
            var inputSignal = realm.GetProperty(inputValue, "signal");
            // The engine's indexer answered a CLR null for an absent property, which the old
            // `?? JSUndefined.Value` turned into undefined; Missing is that same absence.
            signalValue = inputSignal.IsMissing ? JsValue.Undefined : inputSignal;
            inputMode = TryGetJsPropertyString(realm, inputValue, "mode");
            inputCredentials = TryGetJsPropertyString(realm, inputValue, "credentials");
            cache = TryGetJsPropertyString(realm, inputValue, "cache") ?? cache;
            inputRedirect = TryGetJsPropertyString(realm, inputValue, "redirect");
            referrer = TryGetJsPropertyString(realm, inputValue, "referrer") ?? referrer;
            integrity = TryGetJsPropertyString(realm, inputValue, "integrity") ?? integrity;
        }
        else
        {
            url = realm.ToJsString(inputValue);
            body = null;
            headersObject = CreateHeadersObject(realm);
        }

        if (initValue.IsObject)
        {
            methodName = TryGetJsPropertyString(realm, initValue, "method") ?? methodName;
            var initBody = realm.GetProperty(initValue, "body");
            if (!initBody.IsNullish)
                body = ExtractBody(realm, initBody);
            var initHeaders = realm.GetProperty(initValue, "headers");
            if (initHeaders.IsObject)
                headersObject = CreateHeadersObject(realm, initHeaders);
            var initSignal = realm.GetProperty(initValue, "signal");
            if (!initSignal.IsNullish)
                signalValue = initSignal;
            initMode = TryGetJsPropertyString(realm, initValue, "mode");
            initCredentials = TryGetJsPropertyString(realm, initValue, "credentials");
            cache = TryGetJsPropertyString(realm, initValue, "cache") ?? cache;
            initRedirect = TryGetJsPropertyString(realm, initValue, "redirect");
            referrer = TryGetJsPropertyString(realm, initValue, "referrer") ?? referrer;
            integrity = TryGetJsPropertyString(realm, initValue, "integrity") ?? integrity;
        }

        // The same rules fetch() applies (FetchBinding.Requests.cs), so what the getters report is what
        // a fetch of this Request will use: the enums validated and defaulted — a navigate-mode input
        // becomes same-origin, a navigate init is a TypeError — and the method normalized.
        string method, mode, credentials, redirect;
        try
        {
            method = MethodOf(methodName);
            var requestMode = ModeOf(inputMode, initMode);
            if (requestMode == Broiler.Net.Http.RequestMode.NoCors && !Broiler.Net.Http.FetchHeaders.IsCorsSafelistedMethod(method))
                throw new AuthorRequestError($"'{method}' is unsupported in no-cors mode.");
            if (body != null && method is "GET" or "HEAD")
                throw new AuthorRequestError("Request with GET/HEAD method cannot have body.");
            mode = NameOf(requestMode);
            credentials = NameOf(CredentialsOf(inputCredentials, initCredentials));
            redirect = NameOf(RedirectOf(inputRedirect, initRedirect));
        }
        catch (AuthorRequestError error)
        {
            throw realm.Error(JsErrorKind.TypeError, $"Failed to construct 'Request': {error.Message}");
        }

        // The body's own type when the headers name none (Fetch's "new Request" steps).
        if (body?.ContentType is { } bodyType &&
            realm.Invoke(realm.GetProperty(headersObject, "get"), headersObject, [JsValue.String("Content-Type")]).IsNull)
            realm.Invoke(realm.GetProperty(headersObject, "set"), headersObject, [JsValue.String("Content-Type"), JsValue.String(bodyType)]);

        var bytes = body?.Bytes;
        var requestObject = realm.NewObject();
        realm.SetProperty(requestObject, "url", JsValue.String(url));
        realm.SetProperty(requestObject, "method", JsValue.String(method));
        realm.SetProperty(requestObject, "headers", headersObject);
        realm.SetProperty(requestObject, "bodyUsed", JsValue.False);
        realm.SetProperty(requestObject, "body", bytes == null ? JsValue.Null : CreateReadableStreamBody(realm, requestObject, bytes));
        if (bytes != null)
            _requestBodies.AddOrUpdate(IdentityOf(requestObject), new StoredBody(bytes, null));
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
        realm.DefineMethod(requestObject, "clone", 0, JsRegistrationClone098);
        // A Request's body is optional, so every reader here reads the absent one as the empty body.
        var content = bytes ?? [];
        DefineBodyReader(realm, requestObject, "Request", "text", () => JsValue.String(DecodeUtf8(content)));
        DefineBodyReader(realm, requestObject, "Request", "json", () => ParseJsonText(realm, DecodeUtf8(content)));
        DefineBodyReader(realm, requestObject, "Request", "arrayBuffer", () => realm.NewArrayBuffer(content));
        DefineBodyReader(realm, requestObject, "Request", "blob", () => CreateBlobBody(realm, content, headersObject));
        DefineBodyReader(realm, requestObject, "Request", "formData", () => CreateFormDataObject(realm, JsValue.String(DecodeUtf8(content))));

        return requestObject;
    }

    private JsValue CreateResponse(IJsRealm realm, byte[] body, int statusCode, string statusText, string responseUrl, string type, bool redirected, Dictionary<string, string> headers)
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
        realm.SetProperty(responseObject, "body", CreateReadableStreamBody(realm, responseObject, body));
        headers.TryGetValue("Content-Type", out var contentType);
        _responseBodies.AddOrUpdate(IdentityOf(responseObject), new StoredBody(body, contentType));
        // A Response always has a body, even when it is empty, and json() reports a parse failure as
        // its own message — hence ParseResponseJsonText rather than Request's ParseJsonText.
        DefineBodyReader(realm, responseObject, "Response", "text", () => JsValue.String(DecodeUtf8(body)));
        DefineBodyReader(realm, responseObject, "Response", "json", () => ParseResponseJsonText(realm, DecodeUtf8(body)));
        DefineBodyReader(realm, responseObject, "Response", "arrayBuffer", () => realm.NewArrayBuffer(body));
        DefineBodyReader(realm, responseObject, "Response", "blob", () => CreateBlobBody(realm, body, headersObject));
        DefineBodyReader(realm, responseObject, "Response", "formData", () => CreateFormDataObject(realm, JsValue.String(DecodeUtf8(body))));
        JsValue JsRegistrationClone109(in JsCall call)
        {
            if (IsBodyUnavailable(realm, responseObject))
                throw call.Realm.Error(JsErrorKind.Error, "Failed to execute 'clone' on 'Response': body is already used.");
            var clone = CreateResponse(realm, body, statusCode, statusText, responseUrl, type, redirected, new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase));
            if (_bodyReadCallbacks.TryGetValue(IdentityOf(responseObject), out var onRead))
                ReleaseOnBodyRead(clone, onRead);
            return clone;
        }
        realm.DefineMethod(responseObject, "clone", 0, JsRegistrationClone109);

        return responseObject;
    }
}
