namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// A selector list given to <c>querySelector</c>, <c>querySelectorAll</c>, <c>matches</c> or
/// <c>closest</c> matches an element any of its selectors matches (Selectors 4 §4.1), as in Chromium.
/// </summary>
/// <remarks>
/// <b>A list matched nothing.</b> The matcher the bridge asks takes one complex selector, and was
/// handed the whole list: <c>querySelectorAll('div, p')</c> found no element, <c>querySelector</c>
/// answered <c>null</c> and <c>matches</c> false, though each selector alone worked. Every expected
/// answer here is Chromium's, measured on the same page.
/// </remarks>
public class SelectorListQueryTests
{
    private const string PageUrl = "https://example.test/selector-lists";

    private const string Page =
        "<!DOCTYPE html><html><body>" +
        "<div class=\"fill\" id=\"a\"></div><div class=\"red\" id=\"r\"></div><p id=\"p1\">x</p><span class=\"fill red\" id=\"s\"></span>" +
        "<div id=\"out\"></div></body></html>";

    private const string Helpers =
        "function ids(list) { return Array.prototype.map.call(list, function (e) { return e.id; }).join(','); }" +
        "var p1 = document.getElementById('p1');";

    [Theory]
    [InlineData("ids(document.querySelectorAll('div, p'))", "a,r,p1,out")]
    [InlineData("ids(document.querySelectorAll('p, .fill'))", "a,p1,s")]
    [InlineData("ids(document.querySelectorAll('.fill, .red, [id]'))", "a,r,p1,s,out")]
    [InlineData("document.querySelector('p, .red').id", "r")]
    [InlineData("ids(document.body.querySelectorAll('span, p'))", "p1,s")]
    [InlineData("p1.matches('span, p') + ' ' + p1.matches('span, div')", "true false")]
    [InlineData("p1.closest('section, body').tagName", "BODY")]
    [InlineData("ids(document.querySelectorAll('div:not(.red, .fill), span'))", "s,out")]
    public void AnElementMatchesAListWhenItMatchesAnyOfItsSelectors(string expression, string expected) =>
        Assert.Equal(expected, PageProbe.RunGuarded(Page, PageUrl, Helpers, expression));
}
