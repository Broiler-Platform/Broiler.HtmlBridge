using Xunit;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// Tests for the <c>console</c> object methods, verifying presence, arity, undefined return values,
/// grouping methods (<c>group</c>, <c>groupCollapsed</c>, <c>groupEnd</c>), formatting, and safe
/// execution without throwing.
/// </summary>
public class ConsoleBindingTests
{
    private const string PageUrl = "https://example.test/console";
    private const string PageHtml = "<!doctype html><html><body><div id=\"out\"></div></body></html>";

    private static string Run(string expression) =>
        PageProbe.OutOf(PageProbe.Render([PageProbe.GuardedProbe(expression)], PageHtml, PageUrl));

    [Fact]
    public void ConsoleGroupingMethodsExistAndAreFunctions()
    {
        Assert.Equal(
            "group=function collapsed=function end=function log=function debug=function table=function",
            Run("""
                (function () {
                  return 'group=' + (typeof console.group) +
                         ' collapsed=' + (typeof console.groupCollapsed) +
                         ' end=' + (typeof console.groupEnd) +
                         ' log=' + (typeof console.log) +
                         ' debug=' + (typeof console.debug) +
                         ' table=' + (typeof console.table);
                })()
                """));
    }

    [Fact]
    public void ConsoleGroupingCallsReturnUndefinedAndDoNotThrow()
    {
        Assert.Equal(
            "g1=undefined g2=undefined end1=undefined end2=undefined",
            Run("""
                (function () {
                  var r1 = console.group('Outer group', { a: 1 });
                  var r2 = console.groupCollapsed('Inner collapsed');
                  console.log('Inside group');
                  var r3 = console.groupEnd();
                  var r4 = console.groupEnd();
                  return 'g1=' + String(r1) +
                         ' g2=' + String(r2) +
                         ' end1=' + String(r3) +
                         ' end2=' + String(r4);
                })()
                """));
    }

    [Fact]
    public void EmptyConsoleGroupingCallsAreSafe()
    {
        Assert.Equal(
            "emptyGroup=undefined emptyEnd=undefined extraEnd=undefined",
            Run("""
                (function () {
                  var r1 = console.group();
                  var r2 = console.groupEnd();
                  var r3 = console.groupEnd(); // extra groupEnd when no group is open
                  return 'emptyGroup=' + String(r1) +
                         ' emptyEnd=' + String(r2) +
                         ' extraEnd=' + String(r3);
                })()
                """));
    }

    [Fact]
    public void SubWindowConsoleExposesGroupingMethods()
    {
        Assert.Equal(
            "hasGroup=function hasCollapsed=function hasEnd=function runs=true",
            Run("""
                (function () {
                  var frame = document.createElement('iframe');
                  document.body.appendChild(frame);
                  var cw = frame.contentWindow;
                  var c = cw.console;
                  var hasGroup = typeof c.group;
                  var hasCollapsed = typeof c.groupCollapsed;
                  var hasEnd = typeof c.groupEnd;
                  c.group('subframe');
                  c.groupEnd();
                  frame.remove();
                  return 'hasGroup=' + hasGroup +
                         ' hasCollapsed=' + hasCollapsed +
                         ' hasEnd=' + hasEnd +
                         ' runs=true';
                })()
                """));
    }

    [Fact]
    public void CreepJsConsoleProbePattern()
    {
        // Matches the probe pattern in docs/repros/creepjs-platform-probes.html
        Assert.Equal(
            "group=function groupCollapsed=function groupEnd=function log=function",
            Run("""
                (function () {
                  return 'group=' + (typeof console.group) +
                         ' groupCollapsed=' + (typeof console.groupCollapsed) +
                         ' groupEnd=' + (typeof console.groupEnd) +
                         ' log=' + (typeof console.log);
                })()
                """));
    }
}
