// Broiler HtmlBridge — content-rendering polyfills
// version: 1  (Google Search Compliance content-rendering / fidelity stubs)
//
// Pure-JavaScript polyfills installed on a fresh browsing-context global. This asset is embedded in
// Broiler.HtmlBridge.Dom and evaluated once per document by DomBridge.RegisterContentRenderingPolyfills
// (Phase 3 work item 6 — externalized from inline C# string literals, byte-for-byte at the time). Each
// stub is fixed forward here as a real page finds its edges, so this is the definition, not a copy of
// one; the C#-interop polyfills (document.cookie, crypto, DOMException, Node, SVGLength constructors)
// remain in DomBridge/Registration/Registration.cs because they are host-driven, not pure JS.
//
// The bundle continues in text-encoding.js, content-rendering-polyfills.url.js and
// content-rendering-polyfills.abort-and-fonts.js. PolyfillAssets joins the four, in that order, into
// the one script that is evaluated, so a declaration here is still visible to the files after it.

// IntersectionObserver — stub that immediately invokes callback
function IntersectionObserver(callback, options) {
    this._callback = callback;
    this._targets = [];
}
IntersectionObserver.prototype.observe = function(target) {
    this._targets.push(target);
    // Immediately report as intersecting
    var entry = {
        target: target,
        isIntersecting: true,
        intersectionRatio: 1.0,
        boundingClientRect: { top: 0, left: 0, bottom: 0, right: 0, width: 0, height: 0 },
        intersectionRect: { top: 0, left: 0, bottom: 0, right: 0, width: 0, height: 0 },
        rootBounds: null,
        time: 0
    };
    try { this._callback([entry], this); } catch(e) {}
};
IntersectionObserver.prototype.unobserve = function(target) {
    this._targets = this._targets.filter(function(t) { return t !== target; });
};
IntersectionObserver.prototype.disconnect = function() {
    this._targets = [];
};
IntersectionObserver.prototype.takeRecords = function() {
    return [];
};

// ResizeObserver — no-op stub
function ResizeObserver(callback) {
    this._callback = callback;
}
ResizeObserver.prototype.observe = function() {};
ResizeObserver.prototype.unobserve = function() {};
ResizeObserver.prototype.disconnect = function() {};
