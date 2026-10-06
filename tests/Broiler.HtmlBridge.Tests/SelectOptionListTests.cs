using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Dom;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// Taking options out of a select and putting empty ones in, as Chromium does: <c>select.remove(index)</c>,
/// <c>options.remove(index)</c>, and <c>length</c> set on the select or on its options.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing took an option out or put empty ones in.</b> <c>select.remove(1)</c> was the
/// <c>ChildNode.remove()</c> every element has, so it took the select itself out of the page;
/// <c>options.remove()</c> and <c>select.length</c> did not exist; and <c>options.length = 0</c> made an own
/// property of the collection that read 0 from then on, the options all still there.
/// </para>
/// <para>Measured in Chromium, with the same select and the same steps.</para>
/// </remarks>
public class SelectOptionListTests
{
    private const string PageUrl = "https://example.test/select-list";

    private const string Options =
        "<option value=\"a\">A</option><optgroup label=\"g\"><option value=\"b\">B</option><option value=\"c\" selected>C</option></optgroup>" +
        "<option value=\"d\">D</option>";

    private const string Script =
        "var out = document.getElementById('out'), s = document.getElementById('s'), res = [];" +
        "function st() { return Array.from(s.options).map(function (o) { return (o.selected ? '*' : '') + o.value + (o.parentNode.tagName === 'OPTGROUP' ? '@g' : ''); }).join(',') +" +
        "  ' si=' + s.selectedIndex + ' len=' + s.length; }" +
        "function run(name, f) { var r; try { r = f(); res.push(name + ': ' + st() + (r === undefined ? '' : ' ret=' + r)); }" +
        "  catch (e) { res.push(name + ': ERR ' + e.name + ': ' + e.message + ' :: ' + st()); } }" +
        "function reset() { s.innerHTML = '" + Options + "'; }" +
        "run('initial', function () {});" +
        "run('remove(1)', function () { s.remove(1); }); reset();" +
        "run('remove(2)', function () { s.remove(2); }); reset();" +
        "run('remove(-1)', function () { s.remove(-1); }); reset();" +
        "run('remove(9)', function () { s.remove(9); }); reset();" +
        "run(\"remove('1')\", function () { s.remove('1'); }); reset();" +
        "run('remove(1.7)', function () { s.remove(1.7); }); reset();" +
        "run('remove(NaN)', function () { s.remove(NaN); }); reset();" +
        "run('remove(undefined)', function () { s.remove(undefined); }); reset();" +
        "run('remove(null)', function () { s.remove(null); }); reset();" +
        "run('options.remove(0)', function () { s.options.remove(0); }); reset();" +
        "run('options.remove()', function () { s.options.remove(); }); reset();" +
        "run('options.length=2', function () { s.options.length = 2; }); reset();" +
        "run('options.length=6', function () { s.options.length = 6; return Array.from(s.options).slice(4).map(function (o) { return o.outerHTML; }).join('') + ' parent=' + s.options[5].parentNode.tagName; }); reset();" +
        "run('length=1', function () { s.length = 1; }); reset();" +
        "run('options.length=0 then 2', function () { s.options.length = 0; s.options.length = 2; }); reset();" +
        "run('options.length=-1', function () { s.options.length = -1; }); reset();" +
        "run('options.length=100001', function () { s.options.length = 100001; }); reset();" +
        "run('options.length=4.9', function () { s.options.length = 4.9; }); reset();" +
        "run(\"options.length='x'\", function () { s.options.length = 'x'; }); reset();" +
        "run('remove() no args', function () { s.remove(); return 'connected=' + s.isConnected; });" +
        "out.textContent = res.join(' ## ');";

    /// <summary>
    /// <c>new Option(text, value, defaultSelected, selected)</c> as Chromium makes it: a text node only for text
    /// that is not empty, a <c>value</c> attribute only for a value given, a <c>selected</c> attribute for
    /// <c>defaultSelected</c> but selectedness only from <c>selected</c>, which an option keeps in a select and a
    /// later <c>selected</c> attribute still changes;
    /// <c>HTMLOptionElement</c>'s prototype; and no call without <c>new</c>. It did not exist.
    /// </summary>
    [Fact]
    public void NewOptionMakesAnOptionAsChromiumDoes()
    {
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions()));
        using var session = engine.ExecuteInteractive(
            ["var out = document.getElementById('out'), r = [];" +
             "function show(o) { return o.outerHTML + ' sel=' + o.selected + ' defSel=' + o.defaultSelected + ' value=' + o.value + ' text=' + JSON.stringify(o.text) + ' doc=' + (o.ownerDocument === document); }" +
             "r.push(show(new Option()), show(new Option('T')), show(new Option('T', 'v')), show(new Option('T', 'v', true)), show(new Option('T', 'v', true, false))," +
             "  show(new Option('T', 'v', false, true)), show(new Option('T', undefined)), show(new Option(null)), show(new Option('')), show(new Option(5, 6)));" +
             "r.push([Option.length, Option.name, Option.prototype === HTMLOptionElement.prototype, new Option() instanceof HTMLOptionElement, typeof Option].join(' '));" +
             "try { Option(); r.push('no error'); } catch (e) { r.push(e.name + ': ' + e.message); }" +
             "function list(s) { return Array.from(s.options).map(function (o) { return (o.selected ? '*' : '') + o.value; }).join(',') + ' si=' + s.selectedIndex; }" +
             "var s1 = document.createElement('select'); document.body.appendChild(s1);" +
             "s1.add(new Option('A', 'a')); s1.add(new Option('B', 'b', false, true)); s1.add(new Option('C', 'c', true)); r.push(list(s1));" +
             "var s2 = document.createElement('select'); document.body.appendChild(s2); s2.add(new Option('A', 'a')); s2.add(new Option('B', 'b')); r.push(list(s2));" +
             "var o = new Option('X', 'x', false, true); o.selected = false; var s3 = document.createElement('select'); s3.multiple = true;" +
             "document.body.appendChild(s3); s3.add(o); r.push(list(s3));" +
             "var later = new Option('P', 'p'); later.defaultSelected = true; r.push('later ' + later.selected);" +
             "out.textContent = r.join(' ## ');"],
            [], "<html><body><div id=\"out\"></div></body></html>", PageUrl);
        Assert.NotNull(session);

        Assert.Equal(
            [
                "<option></option> sel=false defSel=false value= text=\"\" doc=true",
                "<option>T</option> sel=false defSel=false value=T text=\"T\" doc=true",
                "<option value=\"v\">T</option> sel=false defSel=false value=v text=\"T\" doc=true",
                "<option value=\"v\" selected=\"\">T</option> sel=false defSel=true value=v text=\"T\" doc=true",
                "<option value=\"v\" selected=\"\">T</option> sel=false defSel=true value=v text=\"T\" doc=true",
                "<option value=\"v\">T</option> sel=true defSel=false value=v text=\"T\" doc=true",
                "<option>T</option> sel=false defSel=false value=T text=\"T\" doc=true",
                "<option>null</option> sel=false defSel=false value=null text=\"null\" doc=true",
                "<option></option> sel=false defSel=false value= text=\"\" doc=true",
                "<option value=\"6\">5</option> sel=false defSel=false value=6 text=\"5\" doc=true",
                "0 Option true true function",
                "TypeError: Failed to construct 'Option': Please use the 'new' operator, this DOM object constructor cannot be called as a function.",
                "a,*b,c si=1",
                "*a,b si=0",
                "x si=-1",
                "later true",
            ],
            PageProbe.OutOf(session!.SettleLoadWindow(), decode: true).Split(" ## "));
    }

    /// <summary>Each step from the same select: its options, which is selected, <c>selectedIndex</c> and <c>length</c> after it.</summary>
    [Fact]
    public void OptionsComeAndGoAsInChromium()
    {
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions()));
        using var session = engine.ExecuteInteractive(
            [Script], [], $"<html><body><form id=\"f\"><select id=\"s\" name=\"s\">{Options}</select></form><div id=\"out\"></div></body></html>", PageUrl);
        Assert.NotNull(session);

        var steps = PageProbe.OutOf(session!.SettleLoadWindow(), decode: true).Split(" ## ");

        Assert.Equal(
            [
                "initial: a,b@g,*c@g,d si=2 len=4",
                "remove(1): a,*c@g,d si=1 len=3",
                "remove(2): *a,b@g,d si=0 len=3",
                "remove(-1): a,b@g,*c@g,d si=2 len=4",
                "remove(9): a,b@g,*c@g,d si=2 len=4",
                "remove('1'): a,*c@g,d si=1 len=3",
                "remove(1.7): a,*c@g,d si=1 len=3",
                "remove(NaN): b@g,*c@g,d si=1 len=3",
                "remove(undefined): b@g,*c@g,d si=1 len=3",
                "remove(null): b@g,*c@g,d si=1 len=3",
                "options.remove(0): b@g,*c@g,d si=1 len=3",
                "options.remove(): ERR TypeError: Failed to execute 'remove' on 'HTMLOptionsCollection': 1 argument required, but only 0 present. :: a,b@g,*c@g,d si=2 len=4",
                "options.length=2: *a,b@g si=0 len=2",
                "options.length=6: a,b@g,*c@g,d,, si=2 len=6 ret=<option></option><option></option> parent=SELECT",
                "length=1: *a si=0 len=1",
                "options.length=0 then 2: *, si=0 len=2",
                "options.length=-1: a,b@g,*c@g,d si=2 len=4",
                "options.length=100001: a,b@g,*c@g,d si=2 len=4",
                "options.length=4.9: a,b@g,*c@g,d si=2 len=4",
                "options.length='x':  si=-1 len=0",
                "remove() no args: a,b@g,*c@g,d si=2 len=4 ret=connected=false",
            ],
            steps);
    }
}
