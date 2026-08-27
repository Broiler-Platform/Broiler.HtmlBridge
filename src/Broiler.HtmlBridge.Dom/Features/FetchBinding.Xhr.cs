using Broiler.JavaScript.Engine;

namespace Broiler.HtmlBridge.Dom.Features;

internal sealed partial class FetchBinding
{
    /// <summary>
    /// Registers a basic <c>XMLHttpRequest</c> constructor on the context.
    /// Supports <c>open</c>, <c>send</c>, <c>setRequestHeader</c>,
    /// <c>onreadystatechange</c>, <c>readyState</c>, <c>status</c>, and <c>responseText</c>.
    /// </summary>
    private static void RegisterXMLHttpRequest(JSContext context) => context.Eval(@"
                function XMLHttpRequest() {
                    this.readyState = 0;
                    this.status = 0;
                    this.statusText = '';
                    this.response = null;
                    this.responseText = '';
                    this.responseType = '';
                    this.responseURL = '';
                    this.responseXML = null;
                    this.onreadystatechange = null;
                    this.onload = null;
                    this.onerror = null;
                    this.onabort = null;
                    this.onprogress = null;
                    this.onloadstart = null;
                    this.onloadend = null;
                    this.ontimeout = null;
                    this.withCredentials = false;
                    this.timeout = 0;
                    this._timeoutTimerId = null;
                    this._timedOut = false;
                    this.upload = createXhrUploadTarget();
                    this._method = 'GET';
                    this._url = '';
                    this._async = true;
                    this._headers = {};
                    this._listeners = {};
                    this._responseHeaders = {};
                    this._mimeOverride = null;
                    this._aborted = false;
                    this.UNSENT = 0;
                    this.OPENED = 1;
                    this.HEADERS_RECEIVED = 2;
                    this.LOADING = 3;
                    this.DONE = 4;
                }
                function xhrAddEventListener(type, listener) {
                    if (!type ||
                        (typeof listener !== 'function' &&
                         (!listener || typeof listener.handleEvent !== 'function'))) {
                        return;
                    }
                    var key = '' + type;
                    var listeners = this._listeners[key];
                    if (!listeners) {
                        listeners = [];
                        this._listeners[key] = listeners;
                    }
                    if (listeners.indexOf(listener) < 0) {
                        listeners.push(listener);
                    }
                }
                function xhrRemoveEventListener(type, listener) {
                    if (!type || !listener) return;
                    var key = '' + type;
                    var listeners = this._listeners[key];
                    if (!listeners || !listeners.length) return;
                    for (var i = listeners.length - 1; i >= 0; i--) {
                        if (listeners[i] === listener) {
                            listeners.splice(i, 1);
                            break;
                        }
                    }
                }
                function createXhrEvent(type, init, target) {
                    target = target || null;
                    var event = {
                        type: '' + type,
                        target: target,
                        currentTarget: target,
                        srcElement: target,
                        bubbles: false,
                        cancelable: false,
                        defaultPrevented: false,
                        eventPhase: 2,
                        preventDefault: function() {
                            if (this.cancelable) this.defaultPrevented = true;
                        },
                        stopPropagation: function() {},
                        stopImmediatePropagation: function() {
                            this.__immediateStopped = true;
                        }
                    };
                    if (init) {
                        for (var key in init) {
                            event[key] = init[key];
                        }
                    }
                    return event;
                }
                function xhrDispatchEvent(event) {
                    if (!event || typeof event.type !== 'string') return true;
                    event.target = event.target || this;
                    event.currentTarget = this;
                    event.srcElement = this;
                    event.eventPhase = 2;
                    if (event.bubbles === undefined) event.bubbles = false;
                    if (event.cancelable === undefined) event.cancelable = false;
                    if (event.defaultPrevented === undefined) event.defaultPrevented = false;
                    if (typeof event.preventDefault !== 'function') {
                        event.preventDefault = function() {
                            if (this.cancelable) this.defaultPrevented = true;
                        };
                    }
                    if (typeof event.stopPropagation !== 'function') {
                        event.stopPropagation = function() {};
                    }
                    if (typeof event.stopImmediatePropagation !== 'function') {
                        event.stopImmediatePropagation = function() {
                            this.__immediateStopped = true;
                        };
                    }

                    var handler = this['on' + event.type];
                    if (typeof handler === 'function') {
                        handler.call(this, event);
                    }

                    if (!event.__immediateStopped) {
                        var listeners = this._listeners[event.type];
                        if (listeners && listeners.length) {
                            var snapshot = listeners.slice();
                            for (var i = 0; i < snapshot.length; i++) {
                                var listener = snapshot[i];
                                if (typeof listener === 'function') {
                                    listener.call(this, event);
                                } else if (listener && typeof listener.handleEvent === 'function') {
                                    listener.handleEvent(event);
                                }
                                if (event.__immediateStopped) break;
                            }
                        }
                    }

                    event.currentTarget = null;
                    event.eventPhase = 0;
                    return !event.defaultPrevented;
                }
                function createXhrUploadTarget() {
                    return {
                        _listeners: {},
                        onabort: null,
                        onerror: null,
                        onload: null,
                        onloadend: null,
                        onloadstart: null,
                        onprogress: null,
                        ontimeout: null,
                        addEventListener: xhrAddEventListener,
                        removeEventListener: xhrRemoveEventListener,
                        dispatchEvent: xhrDispatchEvent
                    };
                }
                XMLHttpRequest.UNSENT = 0;
                XMLHttpRequest.OPENED = 1;
                XMLHttpRequest.HEADERS_RECEIVED = 2;
                XMLHttpRequest.LOADING = 3;
                XMLHttpRequest.DONE = 4;
                XMLHttpRequest.prototype.addEventListener = xhrAddEventListener;
                XMLHttpRequest.prototype.removeEventListener = xhrRemoveEventListener;
                XMLHttpRequest.prototype._createEvent = function(type, init, target) {
                    return createXhrEvent(type, init, target || this);
                };
                XMLHttpRequest.prototype._createProgressEvent = function(type, loaded, total, lengthComputable, target) {
                    return this._createEvent(type, {
                        loaded: loaded || 0,
                        total: total || 0,
                        lengthComputable: !!lengthComputable
                    }, target);
                };
                XMLHttpRequest.prototype._getProgressMetrics = function(bodyValue) {
                    if (bodyValue === null || bodyValue === undefined) {
                        return { loaded: 0, total: 0, lengthComputable: false };
                    }
                    if (typeof bodyValue === 'string') {
                        return {
                            loaded: bodyValue.length,
                            total: bodyValue.length,
                            lengthComputable: true
                        };
                    }
                    if (typeof bodyValue.byteLength === 'number') {
                        return {
                            loaded: bodyValue.byteLength,
                            total: bodyValue.byteLength,
                            lengthComputable: true
                        };
                    }
                    if (typeof bodyValue.size === 'number') {
                        return {
                            loaded: bodyValue.size,
                            total: bodyValue.size,
                            lengthComputable: true
                        };
                    }
                    return { loaded: 0, total: 0, lengthComputable: false };
                };
                XMLHttpRequest.prototype.dispatchEvent = xhrDispatchEvent;
                XMLHttpRequest.prototype._dispatchReadyStateChange = function() {
                    this.dispatchEvent(this._createEvent('readystatechange'));
                };
                XMLHttpRequest.prototype.open = function(method, url, isAsync) {
                    this._method = method;
                    this._url = url;
                    this._async = isAsync !== false;
                    this.readyState = 1;
                    this.status = 0;
                    this.statusText = '';
                    this.response = null;
                    this.responseText = '';
                    this.responseXML = null;
                    this.responseURL = '';
                    this._responseHeaders = {};
                    this._aborted = false;
                    this._timedOut = false;
                    if (this._timeoutTimerId !== null) {
                        clearTimeout(this._timeoutTimerId);
                        this._timeoutTimerId = null;
                    }
                    this._dispatchReadyStateChange();
                };
                XMLHttpRequest.prototype.setRequestHeader = function(name, value) {
                    this._headers[name] = value;
                };
                XMLHttpRequest.prototype.getResponseHeader = function(name) {
                    if (!name) return null;
                    var lower = name.toLowerCase();
                    for (var key in this._responseHeaders) {
                        if (key.toLowerCase() === lower) return this._responseHeaders[key];
                    }
                    return null;
                };
                XMLHttpRequest.prototype.getAllResponseHeaders = function() {
                    var result = '';
                    for (var key in this._responseHeaders) {
                        result += key.toLowerCase() + ': ' + this._responseHeaders[key] + '\r\n';
                    }
                    return result;
                };
                XMLHttpRequest.prototype.overrideMimeType = function(mime) {
                    this._mimeOverride = mime;
                };
                XMLHttpRequest.prototype.abort = function() {
                    this._aborted = true;
                    this._timedOut = false;
                    if (this._timeoutTimerId !== null) {
                        clearTimeout(this._timeoutTimerId);
                        this._timeoutTimerId = null;
                    }
                    this.readyState = 0;
                    this.status = 0;
                    this.statusText = '';
                    this.response = null;
                    this.responseText = '';
                    this.responseXML = null;
                    this.dispatchEvent(this._createEvent('abort'));
                    this.dispatchEvent(this._createProgressEvent('loadend', 0, 0, false));
                };
                XMLHttpRequest.prototype.send = function(body) {
                    var self = this;
                    if (self._aborted || self._timedOut) return;
                    if (self._timeoutTimerId !== null) {
                        clearTimeout(self._timeoutTimerId);
                        self._timeoutTimerId = null;
                    }
                    self._timedOut = false;
                    function handleRequestError() {
                        if (self._aborted || self._timedOut) return;
                        if (self._timeoutTimerId !== null) {
                            clearTimeout(self._timeoutTimerId);
                            self._timeoutTimerId = null;
                        }
                        self.readyState = 4;
                        self.status = 0;
                        self.statusText = '';
                        self.response = null;
                        self.responseText = '';
                        self.responseXML = null;
                        self._dispatchReadyStateChange();
                        self.dispatchEvent(self._createEvent('error'));
                        self.dispatchEvent(self._createProgressEvent('loadend', 0, 0, false));
                    }
                    try {
                        var opts = { method: self._method };
                        var requestBody;
                        if (body !== undefined && body !== null &&
                            self._method !== 'GET' && self._method !== 'HEAD') {
                            requestBody = '' + body;
                            opts.body = requestBody;
                        }
                        var hasHeaders = false;
                        for (var k in self._headers) { hasHeaders = true; break; }
                        if (hasHeaders) {
                            opts.headers = self._headers;
                        }
                        if (self.timeout > 0) {
                            self._timeoutTimerId = setTimeout(function() {
                                self._timeoutTimerId = null;
                                if (self._aborted || self._timedOut || self.readyState === 4) return;
                                self._timedOut = true;
                                self.readyState = 4;
                                self.status = 0;
                                self.statusText = '';
                                self.response = null;
                                self.responseText = '';
                                self.responseXML = null;
                                self._dispatchReadyStateChange();
                                self.dispatchEvent(self._createEvent('timeout'));
                                self.dispatchEvent(self._createProgressEvent('loadend', 0, 0, false));
                            }, self.timeout);
                        }
                        if (requestBody !== undefined && self.upload) {
                            var uploadProgress = self._getProgressMetrics(requestBody);
                            self.upload.dispatchEvent(self._createProgressEvent('loadstart', 0, 0, false, self.upload));
                            self.upload.dispatchEvent(self._createProgressEvent('progress', uploadProgress.loaded, uploadProgress.total, uploadProgress.lengthComputable, self.upload));
                            self.upload.dispatchEvent(self._createProgressEvent('load', uploadProgress.loaded, uploadProgress.total, uploadProgress.lengthComputable, self.upload));
                            self.upload.dispatchEvent(self._createProgressEvent('loadend', uploadProgress.loaded, uploadProgress.total, uploadProgress.lengthComputable, self.upload));
                        }
                        self.dispatchEvent(self._createProgressEvent('loadstart', 0, 0, false));
                        fetch(self._url, opts).then(function(response) {
                            if (self._aborted || self._timedOut) return;
                            self.status = response.status;
                            self.statusText = response.statusText;
                            self.responseURL = response.url || self._url;
                            self.readyState = 2;
                            if (response.headers && typeof response.headers.forEach === 'function') {
                                response.headers.forEach(function(value, name) {
                                    self._responseHeaders[name] = value;
                                });
                            }
                            self._dispatchReadyStateChange();
                            var bodyPromise;
                            if (self.responseType === 'arraybuffer' &&
                                response &&
                                typeof response.arrayBuffer === 'function') {
                                bodyPromise = response.arrayBuffer();
                            } else if (self.responseType === 'blob' &&
                                response &&
                                typeof response.blob === 'function') {
                                bodyPromise = response.blob();
                            } else if (self.responseType === 'json' &&
                                response &&
                                typeof response.text === 'function') {
                                bodyPromise = response.text();
                            } else {
                                bodyPromise = response.text();
                            }
                            bodyPromise.then(function(bodyValue) {
                                if (self._aborted || self._timedOut) return;
                                if (self._timeoutTimerId !== null) {
                                    clearTimeout(self._timeoutTimerId);
                                    self._timeoutTimerId = null;
                                }
                                var progress = self._getProgressMetrics(bodyValue);
                                var effectiveMimeType = self._mimeOverride;
                                if (!effectiveMimeType) {
                                    effectiveMimeType = self.getResponseHeader('Content-Type') || '';
                                }
                                var lowerMimeType = (effectiveMimeType || '').toLowerCase();
                                var shouldPopulateResponseXml =
                                    lowerMimeType.indexOf('text/html') >= 0 ||
                                    lowerMimeType.indexOf('application/xhtml+xml') >= 0 ||
                                    lowerMimeType.indexOf('application/xml') >= 0 ||
                                    lowerMimeType.indexOf('text/xml') >= 0 ||
                                    /\+xml(?:\s*;|$)/.test(lowerMimeType);

                                if (self.responseType === '' || self.responseType === 'text') {
                                    // Per XHR semantics, the default/text response types expose
                                    // the same textual payload via both response and responseText.
                                    self.response = bodyValue;
                                    self.responseText = '' + bodyValue;
                                    if (shouldPopulateResponseXml) {
                                        var parsedDocument = document.implementation.createHTMLDocument('');
                                        parsedDocument.body.innerHTML = '' + bodyValue;
                                        self.responseXML = parsedDocument;
                                    } else {
                                        self.responseXML = null;
                                    }
                                } else if (self.responseType === 'document') {
                                    if (shouldPopulateResponseXml) {
                                        var responseDocument = document.implementation.createHTMLDocument('');
                                        responseDocument.body.innerHTML = '' + bodyValue;
                                        self.response = responseDocument;
                                        self.responseXML = responseDocument;
                                    } else {
                                        self.response = null;
                                        self.responseXML = null;
                                    }
                                    self.responseText = '';
                                } else if (self.responseType === 'json') {
                                    try {
                                        self.response = JSON.parse('' + bodyValue);
                                    } catch (e) {
                                        // XHR exposes invalid JSON as a null response for
                                        // responseType='json' while still completing the load path.
                                        self.response = null;
                                    }
                                    self.responseText = '';
                                    self.responseXML = null;
                                } else {
                                    self.response = bodyValue;
                                    self.responseText = '';
                                    self.responseXML = null;
                                }
                                self.readyState = 3;
                                self._dispatchReadyStateChange();
                                self.dispatchEvent(self._createProgressEvent('progress', progress.loaded, progress.total, progress.lengthComputable));
                                self.readyState = 4;
                                self._dispatchReadyStateChange();
                                self.dispatchEvent(self._createProgressEvent('load', progress.loaded, progress.total, progress.lengthComputable));
                                self.dispatchEvent(self._createProgressEvent('loadend', progress.loaded, progress.total, progress.lengthComputable));
                            }, function() {
                                handleRequestError();
                            });
                        }, function() {
                            handleRequestError();
                        });
                    } catch(e) {
                        handleRequestError();
                    }
                };
            ");

}
