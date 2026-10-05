// MessageChannel / MessagePort inside a worker -- HTML §9.5, between two ports of one worker.
//
// A page's channels are the bridge's own (MessagingBinding), delivered on the page's event loop. A
// worker has neither, and a worker script reaches for a channel as readily as a page's does: as a
// scheduler (Closure's goog.async.nextTick posts to one to run a task soon), or to hand one port to
// code of its own. reCAPTCHA's worker threw "MessageChannel is not defined" on its first line.
//
// A message is cloned when it is posted (structuredClone, where the engine has it) and delivered as a
// task of the worker's own event loop, setTimeout(…, 0), never during the post. A port's `onmessage`
// starts it; a port listened to with addEventListener holds its messages until start() is called.
// Transferring a port to another worker or to the page is not modelled: each port stays in this
// worker.
(function (global) {
    'use strict';

    function clone(value) {
        return typeof structuredClone === 'function' ? structuredClone(value) : value;
    }

    function MessagePort() {
        this._other = null;
        this._listeners = [];
        this._queue = [];
        this._started = false;
        this._closed = false;
        this._onmessage = null;
    }

    Object.defineProperty(MessagePort.prototype, 'onmessage', {
        get: function () { return this._onmessage; },
        set: function (handler) {
            this._onmessage = typeof handler === 'function' ? handler : null;
            this.start();
        },
        configurable: true
    });

    MessagePort.prototype.postMessage = function (message) {
        var target = this._other;
        if (this._closed || !target || target._closed)
            return;

        var data = clone(message);
        setTimeout(function () { target._receive(data); }, 0);
    };

    MessagePort.prototype._receive = function (data) {
        if (this._closed)
            return;

        if (!this._started) {
            this._queue.push(data);
            return;
        }

        var event = { type: 'message', data: data, origin: '', lastEventId: '', source: null, ports: [], target: this, currentTarget: this };
        if (this._onmessage)
            this._onmessage.call(this, event);

        var listeners = this._listeners.slice();
        for (var i = 0; i < listeners.length; i++)
            listeners[i].call(this, event);
    };

    MessagePort.prototype.start = function () {
        if (this._started)
            return;

        this._started = true;
        var queued = this._queue;
        this._queue = [];
        var port = this;
        for (var i = 0; i < queued.length; i++)
            (function (data) { setTimeout(function () { port._receive(data); }, 0); })(queued[i]);
    };

    MessagePort.prototype.close = function () {
        this._closed = true;
        this._queue = [];
    };

    MessagePort.prototype.addEventListener = function (type, listener) {
        if (type === 'message' && typeof listener === 'function' && this._listeners.indexOf(listener) < 0)
            this._listeners.push(listener);
    };

    MessagePort.prototype.removeEventListener = function (type, listener) {
        var at = type === 'message' ? this._listeners.indexOf(listener) : -1;
        if (at >= 0)
            this._listeners.splice(at, 1);
    };

    function MessageChannel() {
        this.port1 = new MessagePort();
        this.port2 = new MessagePort();
        this.port1._other = this.port2;
        this.port2._other = this.port1;
    }

    global.MessagePort = MessagePort;
    global.MessageChannel = MessageChannel;
})(this);
