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

    /// <summary>
    /// An option put at an index, through the options or the select itself, as Chromium's <c>SetOption</c> does
    /// it (measured): appended at the end, after empty options up to
    /// an index past it, replacing one inside -- the new one inserted into the select before the option that
    /// followed the old, a <c>NotFoundError</c> when that one is in an optgroup -- and <c>null</c> or
    /// <c>undefined</c> taking one out; anything else a <c>TypeError</c>, sloppy or strict. It made an ordinary
    /// property of the collection and changed nothing, and <c>select[i]</c> did not read.
    /// </summary>
    [Fact]
    public void OptionsTakeIndexedWritesAsInChromium()
    {
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions()));
        using var session = engine.ExecuteInteractive(
            ["var out = document.getElementById('out'), s = document.getElementById('s'), res = [];" +
             "function st() { return Array.from(s.options).map(function (o) { return (o.selected ? '*' : '') + o.value + (o.parentNode.tagName === 'OPTGROUP' ? '@g' : ''); }).join(',') +" +
             "  ' si=' + s.selectedIndex + ' len=' + s.length; }" +
             "function run(name, f) { var r; try { r = f(); res.push(name + ': ' + st() + (r === undefined ? '' : ' ret=' + r)); }" +
             "  catch (e) { res.push(name + ': ERR ' + e.name + ': ' + e.message + ' :: ' + st()); } }" +
             "function reset() { s.innerHTML = '" + Options + "'; }" +
             "function e() { return new Option('E', 'e'); }" +
             "run('reads', function () { return [s[0].value, s[3].value, String(s[4]), String(s[-1]), '0' in s, '3' in s, '4' in s, s.options[1].value].join(','); });" +
             "run('options[len]=e', function () { s.options[s.options.length] = e(); }); reset();" +
             "run('options[len+2]=e', function () { s.options[6] = e(); }); reset();" +
             "run('options[3]=e', function () { s.options[3] = e(); }); reset();" +
             "run('options[2]=e', function () { s.options[2] = e(); }); reset();" +
             "run('options[1]=e', function () { s.options[1] = e(); }); reset();" +
             "run('options[0]=e', function () { s.options[0] = e(); }); reset();" +
             "run('options[1]=options[0]', function () { s.options[1] = s.options[0]; }); reset();" +
             "run('options[1]=null', function () { s.options[1] = null; }); reset();" +
             "run('options[0]=undefined', function () { s.options[0] = undefined; }); reset();" +
             "run('options[10]=null', function () { s.options[10] = null; }); reset();" +
             "run('options[100000]=e', function () { s.options[100000] = e(); }); reset();" +
             "run(\"options['3']=e\", function () { s.options['3'] = e(); }); reset();" +
             "run('select[3]=e', function () { s[3] = e(); }); reset();" +
             "run('select[1]=null', function () { s[1] = null; }); reset();" +
             "run(\"options[0]='x' strict\", function () { 'use strict'; s.options[0] = 'x'; }); reset();" +
             "run('select[0]=5', function () { s[0] = 5; }); reset();" +
             "run('options[0]=div', function () { s.options[0] = document.createElement('div'); }); reset();" +
             "run('interface', function () { return [Object.prototype.toString.call(s.options)," +
             "  Object.getPrototypeOf(s.options) === HTMLOptionsCollection.prototype, Object.getPrototypeOf(HTMLOptionsCollection.prototype) === HTMLCollection.prototype," +
             "  s.options instanceof HTMLCollection, typeof HTMLOptionsCollection, Object.getOwnPropertyNames(HTMLOptionsCollection.prototype).sort().join('/')].join(','); });" +
             "run('length descriptor', function () { var d = Object.getOwnPropertyDescriptor(HTMLOptionsCollection.prototype, 'length');" +
             "  return [typeof d.get, typeof d.set, d.enumerable, d.configurable, Object.keys(s.options).join('/'), s.options.namedItem('x') === null].join(','); });" +
             "run('constructor', function () { new HTMLOptionsCollection(); });" +
             "run('illegal invocation', function () { HTMLOptionsCollection.prototype.add.call({}, e()); });" +
             "run('expando', function () { s.options.foo = 1; return s.options.foo; });" +
             "out.textContent = res.join(' ## ');"],
            [], $"<html><body><form id=\"f\"><select id=\"s\" name=\"s\">{Options}</select></form><div id=\"out\"></div></body></html>", PageUrl);
        Assert.NotNull(session);

        Assert.Equal(
            [
                "reads: a,b@g,*c@g,d si=2 len=4 ret=a,d,undefined,undefined,true,true,false,b",
                "options[len]=e: a,b@g,*c@g,d,e si=2 len=5",
                "options[len+2]=e: a,b@g,*c@g,d,,,e si=2 len=7",
                "options[3]=e: a,b@g,*c@g,e si=2 len=4",
                "options[2]=e: *a,b@g,e,d si=0 len=4",
                "options[1]=e: ERR NotFoundError: Failed to set an indexed property [1] on 'HTMLOptionsCollection': " +
                    "The node before which the new node is to be inserted is not a child of this node. :: a,*c@g,d si=1 len=3",
                "options[0]=e: ERR NotFoundError: Failed to set an indexed property [0] on 'HTMLOptionsCollection': " +
                    "The node before which the new node is to be inserted is not a child of this node. :: b@g,*c@g,d si=1 len=3",
                "options[1]=options[0]: ERR NotFoundError: Failed to set an indexed property [1] on 'HTMLOptionsCollection': " +
                    "The node before which the new node is to be inserted is not a child of this node. :: a,*c@g,d si=1 len=3",
                "options[1]=null: a,*c@g,d si=1 len=3",
                "options[0]=undefined: b@g,*c@g,d si=1 len=3",
                "options[10]=null: a,b@g,*c@g,d si=2 len=4",
                "options[100000]=e: a,b@g,*c@g,d si=2 len=4",
                "options['3']=e: a,b@g,*c@g,e si=2 len=4",
                "select[3]=e: a,b@g,*c@g,e si=2 len=4",
                "select[1]=null: a,*c@g,d si=1 len=3",
                "options[0]='x' strict: ERR TypeError: Failed to set an indexed property [0] on 'HTMLOptionsCollection': " +
                    "parameter 2 is not of type 'HTMLOptionElement'. :: a,b@g,*c@g,d si=2 len=4",
                "select[0]=5: ERR TypeError: Failed to set an indexed property [0] on 'HTMLSelectElement': " +
                    "parameter 2 is not of type 'HTMLOptionElement'. :: a,b@g,*c@g,d si=2 len=4",
                "options[0]=div: ERR TypeError: Failed to set an indexed property [0] on 'HTMLOptionsCollection': " +
                    "parameter 2 is not of type 'HTMLOptionElement'. :: a,b@g,*c@g,d si=2 len=4",
                "interface: a,b@g,*c@g,d si=2 len=4 ret=[object HTMLOptionsCollection],true,true,true,function,add/constructor/length/remove/selectedIndex",
                "length descriptor: a,b@g,*c@g,d si=2 len=4 ret=function,function,true,true,0/1/2/3,true",
                "constructor: ERR TypeError: Failed to construct 'HTMLOptionsCollection': Illegal constructor :: a,b@g,*c@g,d si=2 len=4",
                "illegal invocation: ERR TypeError: Failed to execute 'add' on 'HTMLOptionsCollection': Illegal invocation. :: a,b@g,*c@g,d si=2 len=4",
                "expando: a,b@g,*c@g,d si=2 len=4 ret=1",
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
