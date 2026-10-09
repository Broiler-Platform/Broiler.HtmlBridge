using System.Drawing;
using Xunit;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// Tests for the CSSOM View geometry interfaces (<c>DOMRectReadOnly</c>, <c>DOMRect</c>, and <c>DOMRectList</c>),
/// element box-model geometry (<c>getBoundingClientRect</c>, <c>getClientRects</c>), zero-size attached box semantics,
/// Range geometry, prototype chains, WebIDL receiver checks, and iframe/worker exposure.
/// </summary>
public class DomRectAndGeometryTests
{
    private const string PageUrl = "https://example.test/geometry";
    private const string StandardPageHtml = """
        <!doctype html>
        <html>
        <head>
        <style>
        .zero-box { width: 0; height: 0; }
        .sized-box { width: 100px; height: 50px; }
        .hidden-box { display: none; }
        </style>
        </head>
        <body>
        <div id="sized" class="sized-box"></div>
        <div id="zero" class="zero-box"></div>
        <div id="hidden" class="hidden-box"></div>
        <p id="para">Hello <span>world</span></p>
        <iframe id="sub" srcdoc="hello"></iframe>
        <div id="out"></div>
        </body>
        </html>
        """;

    private static readonly Dictionary<string, RectangleF> DeclaredBoxes = new()
    {
        ["sized"] = new RectangleF(10, 20, 100, 50),
        ["zero"] = new RectangleF(30, 40, 0, 0),
        ["para"] = new RectangleF(0, 100, 200, 30),
    };

    private static ScriptEngine CreateEngine(Dictionary<string, RectangleF>? boxes = null)
    {
        var b = boxes ?? DeclaredBoxes;
        return new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions
        {
            LayoutViewFactory = () => new DeclaredBoxLayoutView(b),
        }));
    }

    private static string RunWithBoxes(string expression, Dictionary<string, RectangleF>? boxes = null)
    {
        var engine = CreateEngine(boxes);
        var probe = PageProbe.GuardedProbe(expression);
        var html = engine.Execute([probe], StandardPageHtml, PageUrl);
        return PageProbe.OutOf(html!, decode: true);
    }

    private static string Run(string expression) =>
        PageProbe.OutOf(PageProbe.Render([PageProbe.GuardedProbe(expression)], StandardPageHtml, PageUrl), decode: true);

    // -------------------------------------------------------------
    // 1. Interface constructors, prototypes, and inheritance
    // -------------------------------------------------------------

    [Theory]
    [InlineData("DOMRectReadOnly", 0)]
    [InlineData("DOMRect", 0)]
    [InlineData("DOMRectList", 0)]
    public void InterfaceConstructorGlobalsExist(string interfaceName, int expectedLength)
    {
        Assert.Equal("function", Run($"typeof {interfaceName}"));
        Assert.Equal("function", Run($"typeof window.{interfaceName}"));
        Assert.Equal(interfaceName, Run($"{interfaceName}.name"));
        Assert.Equal(expectedLength.ToString(), Run($"String({interfaceName}.length)"));
    }

    [Fact]
    public void InterfacePrototypesAndInheritance_MatchWebIdl()
    {
        Assert.Equal("true", Run("Object.getPrototypeOf(DOMRect) === DOMRectReadOnly"));
        Assert.Equal("true", Run("Object.getPrototypeOf(DOMRect.prototype) === DOMRectReadOnly.prototype"));
        Assert.Equal("true", Run("Object.getPrototypeOf(DOMRectReadOnly.prototype) === Object.prototype"));
        Assert.Equal("true", Run("Object.getPrototypeOf(DOMRectList.prototype) === Object.prototype"));
        Assert.Equal("[object DOMRectReadOnly]", Run("Object.prototype.toString.call(DOMRectReadOnly.prototype)"));
        Assert.Equal("[object DOMRect]", Run("Object.prototype.toString.call(DOMRect.prototype)"));
        Assert.Equal("[object DOMRectList]", Run("Object.prototype.toString.call(DOMRectList.prototype)"));
    }

    // -------------------------------------------------------------
    // 2. Constructor contracts and WebIDL invocation checks
    // -------------------------------------------------------------

    [Theory]
    [InlineData("DOMRectReadOnly()", "threw TypeError: Failed to construct 'DOMRectReadOnly': Please use the 'new' operator, this DOM object constructor cannot be called as a function.")]
    [InlineData("DOMRect()", "threw TypeError: Failed to construct 'DOMRect': Please use the 'new' operator, this DOM object constructor cannot be called as a function.")]
    [InlineData("new DOMRectList()", "threw TypeError: Illegal constructor")]
    [InlineData("DOMRectList()", "threw TypeError: Illegal constructor")]
    public void IllegalConstructorCallsThrow(string expression, string expectedError)
    {
        Assert.Equal(expectedError, Run(expression));
    }

    [Fact]
    public void DOMRect_ConstructedValues_AreCorrect()
    {
        Assert.Equal("0,0,0,0,0,0,0,0", Run("""
            (function() {
                var r = new DOMRect();
                return [r.x, r.y, r.width, r.height, r.top, r.right, r.bottom, r.left].join(',');
            })()
            """));

        Assert.Equal("10,20,30,40,20,40,60,10", Run("""
            (function() {
                var r = new DOMRect(10, 20, 30, 40);
                return [r.x, r.y, r.width, r.height, r.top, r.right, r.bottom, r.left].join(',');
            })()
            """));

        Assert.Equal("10,20,-30,-40,-20,10,20,-20", Run("""
            (function() {
                var r = new DOMRect(10, 20, -30, -40);
                return [r.x, r.y, r.width, r.height, r.top, r.right, r.bottom, r.left].join(',');
            })()
            """));
    }

    [Fact]
    public void DOMRect_MutableProperties_UpdateDerivedBounds()
    {
        Assert.Equal("100,200,50,60,200,150,260,100", Run("""
            (function() {
                var r = new DOMRect(1, 2, 3, 4);
                r.x = 100;
                r.y = 200;
                r.width = 50;
                r.height = 60;
                return [r.x, r.y, r.width, r.height, r.top, r.right, r.bottom, r.left].join(',');
            })()
            """));
    }

    [Fact]
    public void DOMRectReadOnly_IsImmutable()
    {
        Assert.Equal("10,20,30,40", Run("""
            (function() {
                var r = new DOMRectReadOnly(10, 20, 30, 40);
                r.x = 999;
                r.y = 999;
                return [r.x, r.y, r.width, r.height].join(',');
            })()
            """));
    }

    [Fact]
    public void DOMRect_ToJSON_ReturnsAllProperties()
    {
        Assert.Equal("{\"x\":1,\"y\":2,\"width\":3,\"height\":4,\"top\":2,\"right\":4,\"bottom\":6,\"left\":1}", Run("""
            JSON.stringify(new DOMRect(1, 2, 3, 4).toJSON())
            """));
    }

    [Fact]
    public void DOMRect_FromRect_StaticMethod()
    {
        Assert.Equal("5,10,15,20", Run("""
            (function() {
                var r = DOMRect.fromRect({ x: 5, y: 10, width: 15, height: 20 });
                return [r.x, r.y, r.width, r.height].join(',');
            })()
            """));

        Assert.Equal("5,10,15,20", Run("""
            (function() {
                var r = DOMRectReadOnly.fromRect({ x: 5, y: 10, width: 15, height: 20 });
                return [r.x, r.y, r.width, r.height].join(',');
            })()
            """));
    }

    // -------------------------------------------------------------
    // 3. Receiver brand checks (Illegal invocation)
    // -------------------------------------------------------------

    [Theory]
    [InlineData("x")]
    [InlineData("y")]
    [InlineData("width")]
    [InlineData("height")]
    [InlineData("top")]
    [InlineData("right")]
    [InlineData("bottom")]
    [InlineData("left")]
    public void DOMRectReadOnly_GettersThrowOnAlienReceiver(string prop)
    {
        Assert.StartsWith("threw TypeError: Failed to read the '" + prop + "' property from 'DOMRectReadOnly': Illegal invocation",
            Run($"Reflect.get(DOMRectReadOnly.prototype, '{prop}', {{}})"));
    }

    [Fact]
    public void DOMRectList_MembersThrowOnAlienReceiver()
    {
        Assert.StartsWith("threw TypeError: Failed to read the 'length' property from 'DOMRectList': Illegal invocation",
            Run("Reflect.get(DOMRectList.prototype, 'length', {})"));
        Assert.StartsWith("threw TypeError: Failed to execute 'item' on 'DOMRectList': Illegal invocation",
            Run("DOMRectList.prototype.item.call({}, 0)"));
    }

    // -------------------------------------------------------------
    // 4. Element getBoundingClientRect and getClientRects
    // -------------------------------------------------------------

    [Fact]
    public void Element_GetBoundingClientRect_ReturnsDOMRectInstance()
    {
        Assert.Equal("true", RunWithBoxes("document.getElementById('sized').getBoundingClientRect() instanceof DOMRect"));
        Assert.Equal("true", RunWithBoxes("document.getElementById('sized').getBoundingClientRect() instanceof DOMRectReadOnly"));
        Assert.Equal("10,20,100,50,20,110,70,10", RunWithBoxes("""
            (function() {
                var r = document.getElementById('sized').getBoundingClientRect();
                return [r.x, r.y, r.width, r.height, r.top, r.right, r.bottom, r.left].join(',');
            })()
            """));
    }

    [Fact]
    public void Element_GetClientRects_ReturnsDOMRectListInstance()
    {
        Assert.Equal("true", RunWithBoxes("document.getElementById('sized').getClientRects() instanceof DOMRectList"));
        Assert.Equal("1", RunWithBoxes("String(document.getElementById('sized').getClientRects().length)"));
        Assert.Equal("true", RunWithBoxes("""
            (function() {
                var list = document.getElementById('sized').getClientRects();
                return list[0] instanceof DOMRect && list.item(0) === list[0];
            })()
            """));
    }

    [Fact]
    public void ZeroSizeAttachedElement_ReturnsRectInDOMRectList()
    {
        // Zero-size attached box must return length 1, not 0!
        Assert.Equal("1", RunWithBoxes("String(document.getElementById('zero').getClientRects().length)"));
        Assert.Equal("30,40,0,0,40,30,40,30", RunWithBoxes("""
            (function() {
                var r = document.getElementById('zero').getClientRects()[0];
                return [r.x, r.y, r.width, r.height, r.top, r.right, r.bottom, r.left].join(',');
            })()
            """));
    }

    [Fact]
    public void DetachedAndHiddenElements_ReturnEmptyDOMRectList()
    {
        Assert.Equal("0", RunWithBoxes("String(document.createElement('div').getClientRects().length)"));
        Assert.Equal("0", RunWithBoxes("String(document.getElementById('hidden').getClientRects().length)"));
        Assert.Equal("0,0,0,0", RunWithBoxes("""
            (function() {
                var r = document.createElement('div').getBoundingClientRect();
                return [r.x, r.y, r.width, r.height].join(',');
            })()
            """));
    }

    [Fact]
    public void DOMRectList_IndexAccess_Item_And_Iteration()
    {
        Assert.Equal("1,item-match,out-of-bounds-null,index-undefined,iterated-1", RunWithBoxes("""
            (function() {
                var list = document.getElementById('sized').getClientRects();
                var len = list.length;
                var itemMatch = list[0] === list.item(0) ? 'item-match' : 'mismatch';
                var oob = list.item(99) === null ? 'out-of-bounds-null' : 'oob-fail';
                var undef = list[99] === undefined ? 'index-undefined' : 'undef-fail';
                var count = 0;
                for (var r of list) { count++; }
                return [len, itemMatch, oob, undef, 'iterated-' + count].join(',');
            })()
            """));
    }

    // -------------------------------------------------------------
    // 5. Range getBoundingClientRect and getClientRects
    // -------------------------------------------------------------

    [Fact]
    public void Range_GetBoundingClientRect_And_GetClientRects()
    {
        Assert.Equal("true,true,1", RunWithBoxes("""
            (function() {
                var range = document.createRange();
                range.selectNode(document.getElementById('sized'));
                var bRect = range.getBoundingClientRect();
                var rects = range.getClientRects();
                return [bRect instanceof DOMRect, rects instanceof DOMRectList, rects.length].join(',');
            })()
            """));
    }

    [Fact]
    public void Range_Collapsed_ReturnsEmptyListAndZeroRect()
    {
        Assert.Equal("0,0,0,0,0", RunWithBoxes("""
            (function() {
                var range = document.createRange();
                var list = range.getClientRects();
                var b = range.getBoundingClientRect();
                return [list.length, b.x, b.y, b.width, b.height].join(',');
            })()
            """));
    }

    // -------------------------------------------------------------
    // 6. Sub-window (iframe) mirroring
    // -------------------------------------------------------------

    [Fact]
    public void SubWindow_MirrorsGeometryGlobals()
    {
        Assert.Equal("function,function,function", Run("""
            (function() {
                var sub = document.getElementById('sub').contentWindow;
                return [typeof sub.DOMRect, typeof sub.DOMRectReadOnly, typeof sub.DOMRectList].join(',');
            })()
            """));
    }
}
