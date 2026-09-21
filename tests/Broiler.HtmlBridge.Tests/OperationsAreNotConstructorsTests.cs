namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// Web IDL §3.7.4: an operation is a plain function. It has no <c>prototype</c> property and
/// <c>new op()</c> throws. Only an interface object is constructable.
/// <para>
/// The bridge mints a member either through the realm's method factory or its constructor factory,
/// and the difference is observable twice over: a constructor carries a <c>prototype</c> object with
/// a <c>constructor</c> back-reference that no page can reach, and it answers <c>new</c>. For a while
/// most of the document's operations, every <c>Headers</c>/<c>FormData</c>/<c>Response</c> member and
/// a dozen stubs were minted the constructable way, which is both a conformance bug a page can see
/// and a per-object allocation on the fetch types, whose members are installed on a fresh object
/// rather than on a shared prototype.
/// </para>
/// </summary>
public class OperationsAreNotConstructorsTests
{
    private const string PageUrl = "https://example.test/operations";

    private const string PageHtml =
        "<html><body><div id=\"host\"><span id=\"child\">c</span></div><div id=\"out\"></div></body></html>";

    private static string Run(string expression) =>
        PageProbe.OutOf(PageProbe.Render([PageProbe.GuardedProbe(expression)], PageHtml, PageUrl));

    /// <summary>An operation exposes no <c>prototype</c>, so a page reads <c>undefined</c>.</summary>
    [Theory]
    // the document's own operations, which were the largest group minted constructable
    [InlineData("document.createElement")]
    [InlineData("document.createTextNode")]
    [InlineData("document.getElementById")]
    [InlineData("document.getElementsByTagName")]
    [InlineData("document.querySelector")]
    [InlineData("document.querySelectorAll")]
    [InlineData("document.importNode")]
    [InlineData("document.addEventListener")]
    [InlineData("document.write")]
    // window-level operations
    [InlineData("window.getComputedStyle")]
    [InlineData("window.fetch")]
    [InlineData("window.setTimeout")]
    // element operations, which were already correct and must stay so
    [InlineData("document.body.appendChild")]
    [InlineData("document.body.addEventListener")]
    public void AnOperationHasNoPrototype(string operation) =>
        Assert.Equal("undefined", Run($"typeof {operation}.prototype"));

    /// <summary><c>new op()</c> is a TypeError, whatever the operation would have returned.</summary>
    [Theory]
    [InlineData("new document.createElement('p')")]
    [InlineData("new document.getElementById('host')")]
    [InlineData("new document.querySelector('#host')")]
    [InlineData("new document.body.appendChild(document.createElement('i'))")]
    [InlineData("new window.getComputedStyle(document.body)")]
    public void NewOnAnOperationThrowsTypeError(string expression) =>
        Assert.StartsWith("threw TypeError", Run($"(function () {{ {expression}; return 'no-throw'; }})()"));

    /// <summary>The members of a fetch object are operations too, and those live on the object itself.</summary>
    [Theory]
    [InlineData("new Headers().get")]
    [InlineData("new Headers().has")]
    [InlineData("new Headers().append")]
    [InlineData("new FormData().get")]
    [InlineData("new FormData().toString")]
    public void AFetchObjectsMembersAreOperations(string operation) =>
        Assert.Equal("undefined", Run($"typeof {operation}.prototype"));

    /// <summary>
    /// The interface objects a page may legitimately <c>new</c> stay constructable — the point of the
    /// distinction is that these are the ones that carry a prototype.
    /// </summary>
    [Theory]
    [InlineData("FormData")]
    [InlineData("Headers")]
    [InlineData("Request")]
    [InlineData("Response")]
    [InlineData("MessageChannel")]
    [InlineData("CSSStyleSheet")]
    public void AnInterfaceObjectIsStillConstructable(string interfaceObject)
    {
        Assert.Equal("function", Run($"typeof {interfaceObject}"));
        Assert.Equal("object", Run($"typeof {interfaceObject}.prototype"));
    }

    [Fact]
    public void TheInterfaceObjectsStillConstruct()
    {
        Assert.Equal("[object Object]", Run("Object.prototype.toString.call(new Headers()) === '[object Object]' ? '[object Object]' : String(new Headers())"));
        Assert.Equal("0", Run("String(new FormData().toString().length)"));
        Assert.Equal("object", Run("typeof new MessageChannel()"));
    }

    /// <summary>The operations still work, which is the part that matters most.</summary>
    [Fact]
    public void TheOperationsThemselvesStillWork()
    {
        Assert.Equal("SPAN", Run("document.querySelector('#child').tagName"));
        Assert.Equal("host", Run("document.getElementById('host').id"));
        Assert.Equal("P", Run("document.createElement('p').tagName"));
        Assert.Equal("hi", Run("(function () { var t = document.createTextNode('hi'); document.body.appendChild(t); return t.data; })()"));
        Assert.Equal("1", Run("String(document.getElementsByTagName('span').length)"));
    }
}
