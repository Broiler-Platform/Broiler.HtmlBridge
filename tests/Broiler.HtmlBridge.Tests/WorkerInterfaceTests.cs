using System;
using System.Text.Json;
using Broiler.Net.Http;
using Reply = Broiler.HtmlBridge.Tests.LoopbackCookieServer.Reply;
using Xunit;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// Tests for the Worker environment interfaces (HTML §10.1–10.3 and Device Memory):
/// <c>WorkerGlobalScope</c>, <c>DedicatedWorkerGlobalScope</c>, <c>WorkerNavigator</c>,
/// and <c>WorkerLocation</c>.
/// Verifies constructor behavior, prototype chain, brand checks, descriptors, illegal invocation,
/// same-object behavior, and worker-only exposure.
/// </summary>
public class WorkerInterfaceTests
{
    private static BrowserNetworkSession NewProfile() =>
        new(new BrowserNetworkSessionOptions { Timeout = TimeSpan.FromSeconds(20) });

    private static string RunWorker(string workerBody)
    {
        using var server = new LoopbackCookieServer();
        server.Map("/worker.js", new Reply(ContentType: "text/javascript", Body: workerBody));

        using var profile = NewProfile();
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions { Network = profile, Cookies = profile }));
        var script =
            "var out = document.getElementById('out');" +
            "var worker = new Worker('/worker.js');" +
            "worker.onmessage = function (e) { out.textContent = String(e.data); };" +
            "worker.onerror = function (e) { out.textContent = 'worker error: ' + e.message; };" +
            "worker.postMessage('start');";

        using var session = engine.ExecuteInteractive(
            [script],
            [],
            "<html><head></head><body><div id=\"out\">waiting</div></body></html>",
            server.LocalhostUrl("/page"));

        return PageProbe.OutOf(session!.SettleLoadWindow(), decode: true);
    }

    private static string RunOnWindow(string expression)
    {
        using var profile = NewProfile();
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions { Network = profile, Cookies = profile }));
        using var session = engine.ExecuteInteractive(
            [$"document.getElementById('out').textContent = String({expression});"],
            [],
            "<html><head></head><body><div id=\"out\">waiting</div></body></html>",
            "https://example.test/page");

        return PageProbe.OutOf(session!.SettleLoadWindow(), decode: true);
    }

    [Fact]
    public void WorkerGlobalInterfaces_ExistAndAreFunctions()
    {
        var result = RunWorker(
            "onmessage = function () {" +
            " postMessage([" +
            "  typeof WorkerGlobalScope," +
            "  typeof DedicatedWorkerGlobalScope," +
            "  typeof WorkerNavigator," +
            "  typeof WorkerLocation" +
            " ].join('|'));" +
            "};");

        Assert.Equal("function|function|function|function", result);
    }

    [Fact]
    public void WorkerGlobalScope_PrototypeHierarchyAndBranding()
    {
        var result = RunWorker(
            "onmessage = function () {" +
            " postMessage([" +
            "  self instanceof DedicatedWorkerGlobalScope," +
            "  self instanceof WorkerGlobalScope," +
            "  self.WorkerGlobalScope === WorkerGlobalScope," +
            "  self.DedicatedWorkerGlobalScope === DedicatedWorkerGlobalScope," +
            "  Object.prototype.toString.call(self)," +
            "  Object.prototype.toString.call(DedicatedWorkerGlobalScope.prototype)," +
            "  Object.prototype.toString.call(WorkerGlobalScope.prototype)" +
            " ].join('|'));" +
            "};");

        Assert.Equal("true|true|true|true|[object DedicatedWorkerGlobalScope]|[object DedicatedWorkerGlobalScope]|[object WorkerGlobalScope]", result);
    }

    [Fact]
    public void WorkerInterfaces_ThrowOnDirectConstruction()
    {
        var result = RunWorker(
            "onmessage = function () {" +
            " function attempt(fn) { try { fn(); return 'ok'; } catch (e) { return e.name + ': ' + e.message; } }" +
            " postMessage([" +
            "  attempt(function () { new WorkerGlobalScope(); })," +
            "  attempt(function () { new DedicatedWorkerGlobalScope(); })," +
            "  attempt(function () { new WorkerNavigator(); })," +
            "  attempt(function () { new WorkerLocation(); })" +
            " ].join('|'));" +
            "};");

        Assert.Contains("TypeError: Failed to construct 'WorkerGlobalScope': Illegal constructor", result);
        Assert.Contains("TypeError: Failed to construct 'DedicatedWorkerGlobalScope': Illegal constructor", result);
        Assert.Contains("TypeError: Failed to construct 'WorkerNavigator': Illegal constructor", result);
        Assert.Contains("TypeError: Failed to construct 'WorkerLocation': Illegal constructor", result);
    }

    [Fact]
    public void WorkerNavigator_InstanceAndIdentityContracts()
    {
        var result = RunWorker(
            "onmessage = function () {" +
            " postMessage([" +
            "  typeof navigator," +
            "  navigator instanceof WorkerNavigator," +
            "  self.navigator === navigator," +
            "  Object.prototype.toString.call(navigator)," +
            "  Object.getOwnPropertyNames(navigator).length," +
            "  navigator.appCodeName," +
            "  navigator.appName," +
            "  navigator.product," +
            "  navigator.productSub," +
            "  navigator.platform," +
            "  navigator.vendor," +
            "  navigator.webdriver," +
            "  navigator.language," +
            "  Array.isArray(navigator.languages)," +
            "  navigator.onLine," +
            "  navigator.hardwareConcurrency > 0," +
            "  navigator.deviceMemory > 0," +
            "  navigator.userAgent.length > 0," +
            "  navigator.appVersion.length > 0" +
            " ].join('|'));" +
            "};");

        Assert.Equal("object|true|true|[object WorkerNavigator]|0|Mozilla|Netscape|Gecko|20030107|Win32||true|en-US|true|true|true|true|true|true", result);
    }

    [Fact]
    public void WorkerNavigator_PrototypeAccessorsHaveCorrectDescriptors()
    {
        var result = RunWorker(
            "onmessage = function () {" +
            " var props = ['userAgent', 'platform', 'hardwareConcurrency', 'deviceMemory', 'language', 'onLine'];" +
            " var allMatch = props.every(function (p) {" +
            "  var d = Object.getOwnPropertyDescriptor(WorkerNavigator.prototype, p);" +
            "  return d && typeof d.get === 'function' && typeof d.set === 'undefined' && d.enumerable && d.configurable;" +
            " });" +
            " postMessage(allMatch);" +
            "};");

        Assert.Equal("true", result);
    }

    [Fact]
    public void WorkerNavigator_PrototypeGettersRejectIncompatibleReceivers()
    {
        var result = RunWorker(
            "onmessage = function () {" +
            " function attempt(prop, receiver) {" +
            "  var getter = Object.getOwnPropertyDescriptor(WorkerNavigator.prototype, prop).get;" +
            "  try { getter.call(receiver); return 'ok'; } catch (e) { return e.name; }" +
            " }" +
            " postMessage([" +
            "  attempt('userAgent', WorkerNavigator.prototype)," +
            "  attempt('userAgent', {})," +
            "  attempt('userAgent', null)," +
            "  attempt('hardwareConcurrency', WorkerNavigator.prototype)," +
            "  attempt('deviceMemory', {})," +
            "  attempt('platform', Object.create(navigator))" +
            " ].join('|'));" +
            "};");

        Assert.Equal("TypeError|TypeError|TypeError|TypeError|TypeError|TypeError", result);
    }

    [Fact]
    public void DedicatedWorkerGlobalScope_ScopeDetectionAndMessaging()
    {
        var result = RunWorker(
            "onmessage = function () {" +
            " var isWorkerScope = !self.document && self.WorkerGlobalScope;" +
            " postMessage([" +
            "  Boolean(isWorkerScope)," +
            "  typeof self.document," +
            "  self.name," +
            "  typeof self.location," +
            "  self.location instanceof WorkerLocation" +
            " ].join('|'));" +
            "};");

        Assert.Equal("true|undefined|/worker.js|object|true", result);
    }

    [Fact]
    public void WorkerInterfaces_AreNotExposedOnWindow()
    {
        Assert.Equal("undefined", RunOnWindow("typeof window.WorkerGlobalScope"));
        Assert.Equal("undefined", RunOnWindow("typeof window.DedicatedWorkerGlobalScope"));
        Assert.Equal("undefined", RunOnWindow("typeof window.WorkerNavigator"));
        Assert.Equal("undefined", RunOnWindow("typeof window.WorkerLocation"));
    }

    [Fact]
    public void Worker_AddAndRemoveEventListenerWork()
    {
        var result = RunWorker(
            "var count = 0;" +
            "function h1(e) { count += 1; }" +
            "function h2(e) { count += 10; postMessage('count:' + count); }" +
            "addEventListener('message', h1);" +
            "addEventListener('message', h2);" +
            "removeEventListener('message', h1);" +
            "");

        Assert.Equal("count:10", result);
    }

    [Fact]
    public void Worker_Constructor_PrototypeHierarchyAndInstanceIdentity()
    {
        var result = RunOnWindow(
            "(function () {" +
            "  var w = new Worker('data:text/javascript,');" +
            "  var hasConstructor = function (x, name) { return x && x.__proto__.constructor.name == name; };" +
            "  var reports = [" +
            "    typeof window.Worker," +
            "    Worker.name," +
            "    Object.prototype.toString.call(Worker.prototype)," +
            "    Object.getPrototypeOf(Worker.prototype) === EventTarget.prototype," +
            "    w instanceof Worker," +
            "    w instanceof EventTarget," +
            "    hasConstructor(w, 'Worker')," +
            "    w.__proto__ === Worker.prototype," +
            "    Object.prototype.toString.call(w)" +
            "  ];" +
            "  w.terminate();" +
            "  return reports.join('|');" +
            "})()");

        Assert.Equal("function|Worker|[object Worker]|true|true|true|true|true|[object Worker]", result);
    }

    [Fact]
    public void Worker_Constructor_ThrowsWhenCalledWithoutNew()
    {
        var result = RunOnWindow(
            "(function () {" +
            "  try {" +
            "    Worker('data:text/javascript,');" +
            "    return 'no error';" +
            "  } catch (e) {" +
            "    return (e instanceof TypeError) + '|' + (e.message.indexOf('new') !== -1);" +
            "  }" +
            "})()");

        Assert.Equal("true|true", result);
    }
}
