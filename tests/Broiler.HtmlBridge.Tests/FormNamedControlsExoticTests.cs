using Broiler.HtmlBridge;

namespace Broiler.Browser.Core.Tests;

/// <summary>
/// The two objects in the forms binding whose property lookup the host completes: the
/// <c>&lt;form&gt;</c> wrapper and its <c>elements</c> collection, both of which resolve an unknown
/// name to the control carrying it (HTML §4.10.3, §4.10.11).
/// <para>
/// They were a pair of <c>JSObject</c> subclasses overriding the engine's lookup; they are now an
/// <c>IJsExotic</c> the realm consults, and the realm — not the binding — is what puts ordinary
/// properties first. <b>That order is the whole reason these tests exist.</b> Getting it backwards is
/// silently wrong rather than loudly wrong: a form containing a control named <c>submit</c> would
/// start shadowing <c>form.submit()</c>, and nothing would fail until a page's search box stopped
/// working. There is no compiler error for it and no crash, so there is a test.
/// </para>
/// <para>
/// The second thing pinned here is what the collection <em>enumerates</em>, because the migration
/// could have changed it invisibly. The controls' names are deliberately not offered as supported
/// names and the indices are deliberately a snapshot installed as ordinary properties, so
/// <c>Object.keys</c>, <c>for…in</c> and a spread see exactly what they saw before the exotic
/// existed.
/// </para>
/// </summary>
public class FormNamedControlsExoticTests
{
    private const string PageUrl = "https://example.test/page";

    /// <summary>
    /// Evaluates <paramref name="expression"/> against <paramref name="html"/> and answers its
    /// string value, by parking it on an attribute the serializer writes out — the only channel a
    /// non-interactive run has.
    /// </summary>
    private static string Probe(string html, string expression)
    {
        string serialized = new ScriptEngine().Execute(
            [$"document.body.setAttribute('data-probe', String({expression}));"], html, PageUrl) ?? string.Empty;

        const string marker = "data-probe=\"";
        int start = serialized.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"the probe did not run; serialized document was:\n{serialized}");

        start += marker.Length;
        return serialized[start..serialized.IndexOf('"', start)];
    }

    /// <summary>A form whose controls are named after members the form and the collection already have.</summary>
    private const string CollidingForm =
        """
        <html><body><form action="/search">
          <input name="q" value="typed">
          <input name="submit" value="collides with the method">
          <input name="length" value="collides with the collection's length">
        </form></body></html>
        """;

    [Fact]
    public void AControlNamedSubmitDoesNotShadowTheFormsSubmitMethod()
    {
        // The case IJsExotic.cs states in as many words, in the shape it actually reaches a page:
        // `form.submit()` is how a script submits, and a form with a submit button named "submit"
        // is the commonest markup there is.
        Assert.Equal("function", Probe(CollidingForm, "typeof document.forms[0].submit"));
    }

    [Fact]
    public void AControlNamedActionDoesNotShadowTheFormsActionAttribute()
    {
        Assert.Equal(
            "/search",
            Probe(
                """
                <html><body><form action="/search"><input name="action" value="collides"></form></body></html>
                """,
                "document.forms[0].getAttribute('action')"));

        // …and the IDL attribute itself, which is the property a page reads.
        Assert.Equal("/search", Probe(CollidingForm, "document.forms[0].action"));
    }

    [Fact]
    public void AControlNamedLengthDoesNotShadowTheCollectionsLength()
    {
        Assert.Equal("number", Probe(CollidingForm, "typeof document.forms[0].elements.length"));
        Assert.Equal("3", Probe(CollidingForm, "document.forms[0].elements.length"));
    }

    [Fact]
    public void ANameNoOrdinaryPropertyAnswersReachesTheControl()
    {
        // Both objects offer named access, and they resolve through the one handler, so they cannot
        // disagree about which control a name means.
        Assert.Equal("typed", Probe(CollidingForm, "document.forms[0].q.value"));
        Assert.Equal("typed", Probe(CollidingForm, "document.forms[0].elements.q.value"));
    }

    [Fact]
    public void AMissAnswersUndefinedOnTheFormAndNullOnTheCollection()
    {
        // The one thing the two callers of the handler disagree about, and both answers are the ones
        // they gave before the exotic: an absent named property on the form is an absent property,
        // while the collection reports null.
        Assert.Equal("undefined", Probe(CollidingForm, "typeof document.forms[0].nothingIsNamedThis"));
        Assert.Equal("null", Probe(CollidingForm, "document.forms[0].elements.nothingIsNamedThis"));
    }

    [Fact]
    public void TheCollectionEnumeratesItsIndicesAndLengthAndNotItsControlNames()
    {
        // Object.keys, for…in and a spread all read the same own-key enumeration, and all three must
        // answer what they answered before: the index snapshot and `length`. The controls' names are
        // NOT supported names — a browser does enumerate them, and adding them here would be a
        // behaviour change rather than the refactor this is.
        const string keys = "document.forms[0].elements";

        Assert.Equal("0|1|2|length", Probe(CollidingForm, $"Object.keys({keys}).join('|')"));

        Assert.Equal(
            "0|1|2|length",
            Probe(CollidingForm, $"(function () {{ var seen = []; for (var k in {keys}) {{ seen.push(k); }} return seen.join('|'); }})()"));

        Assert.Equal("0|1|2|length", Probe(CollidingForm, $"Object.keys(Object.assign({{}}, {keys})).join('|')"));
    }

    [Fact]
    public void TheCollectionStillIndexesItsControlsInTreeOrder()
    {
        Assert.Equal("q|submit|length", Probe(CollidingForm, "[0, 1, 2].map(function (i) { return document.forms[0].elements[i].name; }).join('|')"));
    }
}
