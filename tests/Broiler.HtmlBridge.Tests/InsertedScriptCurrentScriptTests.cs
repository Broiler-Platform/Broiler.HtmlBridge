namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// While a script the page inserted runs, <c>document.currentScript</c> is that script, and the one
/// before it is restored after (HTML §4.12.1.1, "execute the script element").
/// </summary>
/// <remarks>
/// Only the document's own scripts set it, so an inserted external script read <c>null</c> and an
/// inserted inline script read the script that inserted it. Next.js's Turbopack runtime registers
/// every chunk it loads with <c>document.currentScript</c> and throws "chunk path empty but not in a
/// worker" when it is null, so duckduckgo.com never rendered its content. The external scripts here
/// use <c>data:</c> URLs, which the runner decodes on the event loop the probe drains.
/// </remarks>
public class InsertedScriptCurrentScriptTests
{
    private const string PageUrl = "https://example.test/current-script";
    private const string Page = "<html><body><div id=\"out\">nothing</div></body></html>";

    /// <summary>Defines <c>cs()</c>: the id of <c>document.currentScript</c>, or "null".</summary>
    private const string Helper =
        "window.cs = function () { return document.currentScript ? (document.currentScript.id || 'no-id') : 'null'; };";

    private static string Run(string script) =>
        PageProbe.OutOf(PageProbe.Render([Helper + script], Page, PageUrl));

    /// <summary>Appends a script whose <c>src</c> is a <c>data:</c> URL carrying <paramref name="program"/>.</summary>
    private static string InsertExternal(string id, string program) =>
        "var s = document.createElement('script');" +
        $"s.id = '{id}';" +
        $"s.src = 'data:text/javascript,' + encodeURIComponent('{program}');" +
        "document.body.appendChild(s);";

    [Fact]
    public void An_Inserted_External_Script_Is_The_Current_Script()
    {
        Assert.Equal(
            "ext",
            Run(InsertExternal("ext", "document.getElementById(\"out\").textContent = cs();")));
    }

    /// <summary>What Turbopack does with it: read the chunk's own <c>src</c> back off the element.</summary>
    [Fact]
    public void An_Inserted_External_Scripts_Src_Is_Read_Back_As_Turbopack_Reads_It()
    {
        Assert.Equal(
            "true",
            Run(
                "window.assigned = 'data:text/javascript,' + encodeURIComponent('document.getElementById(\"out\").textContent = String(document.currentScript.getAttribute(\"src\") === window.assigned);');" +
                "var s = document.createElement('script');" +
                "s.src = window.assigned;" +
                "document.body.appendChild(s);"));
    }

    /// <summary>
    /// An inline script runs inside the <c>appendChild</c> that inserts it: it is the current script
    /// there, and the inserting script is the current script again once it returns.
    /// </summary>
    [Fact]
    public void An_Inserted_Inline_Script_Is_The_Current_Script_Until_It_Returns()
    {
        var parts = Run(
            "var before = cs();" +
            "var s = document.createElement('script');" +
            "s.id = 'inner';" +
            "s.textContent = 'window.innerSaw = cs();';" +
            "document.body.appendChild(s);" +
            "document.getElementById('out').textContent = [before, window.innerSaw, cs()].join(',');").Split(',');

        Assert.Equal("inner", parts[1]);
        Assert.Equal(parts[0], parts[2]);
    }

    [Fact]
    public void The_Inserting_Script_Is_Restored_When_The_Inserted_One_Throws()
    {
        var parts = Run(
            "var before = cs();" +
            "var s = document.createElement('script');" +
            "s.id = 'thrower';" +
            "s.textContent = 'window.throwerSaw = cs(); throw new Error(\"thrown on purpose\");';" +
            "document.body.appendChild(s);" +
            "document.getElementById('out').textContent = [window.throwerSaw, before === cs()].join(',');").Split(',');

        Assert.Equal(["thrower", "true"], parts);
    }

    /// <summary>An external script that inserts an inline one: each is current in turn, and the outer again after.</summary>
    [Fact]
    public void Inserted_Scripts_Nest()
    {
        Assert.Equal(
            "ext,inner,ext",
            Run(InsertExternal(
                "ext",
                "var log = [cs()];" +
                "var i = document.createElement(\"script\");" +
                "i.id = \"inner\";" +
                "i.textContent = \"window.innerSaw = cs();\";" +
                "document.body.appendChild(i);" +
                "log.push(window.innerSaw, cs());" +
                "document.getElementById(\"out\").textContent = log.join(\",\");")));
    }

    /// <summary>The control: outside any script, in a timer the inserted script set, there is no current script.</summary>
    [Fact]
    public void A_Timer_Callback_Has_No_Current_Script()
    {
        Assert.Equal(
            "null",
            Run(InsertExternal(
                "ext",
                "setTimeout(function () { document.getElementById(\"out\").textContent = cs(); }, 0);")));
    }
}
