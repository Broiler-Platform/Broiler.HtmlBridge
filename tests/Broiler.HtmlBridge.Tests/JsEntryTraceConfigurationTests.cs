using Broiler.HtmlBridge.Core.Diagnostics;

namespace Broiler.Browser.Core.Tests;

/// <summary>
/// The <c>BROILER_TRACE_JS_ENTRY</c> contract: what a reader types, and what they get.
/// </summary>
/// <remarks>
/// This is the half of the tracer a person interacts with, and the half where a mistake is
/// expensive out of proportion to its size. The trace exists to be switched on for one
/// reproduction of a bug that takes a page load and twenty seconds of waiting to provoke; a value
/// that silently disabled it, or that quietly set a threshold so high nothing was reported, would
/// cost that reproduction and look identical to "the problem did not happen this time".
/// <para>
/// Only the parse is covered here. Activation itself is process-wide state read once at type
/// initialization, so it cannot be exercised twice in one test run — which is exactly why the
/// contract was separated from it.
/// </para>
/// </remarks>
public class JsEntryTraceConfigurationTests
{
    private const double DefaultTurnMs = 250;
    private const double DefaultGapMs = 1000;

    // Unset is the production case: every run that did not ask for a trace.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("FALSE")]
    [InlineData("off")]
    [InlineData("Off")]
    public void AnAbsentOrDisablingValue_LeavesItOff(string? raw)
    {
        Assert.False(JsEntryTrace.TryParseConfiguration(raw, out var turnMs, out var gapMs));

        // The thresholds are still the defaults rather than zero, so a caller that reads them
        // without checking the return value cannot end up reporting every turn it sees.
        Assert.Equal(DefaultTurnMs, turnMs);
        Assert.Equal(DefaultGapMs, gapMs);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("on")]
    [InlineData("  on  ")]
    public void APlainTruthyValue_EnablesTheDefaults(string raw)
    {
        Assert.True(JsEntryTrace.TryParseConfiguration(raw, out var turnMs, out var gapMs));
        Assert.Equal(DefaultTurnMs, turnMs);
        Assert.Equal(DefaultGapMs, gapMs);
    }

    // A bare number is the shape a reader reaches for first when the defaults are too noisy.
    [Theory]
    [InlineData("500", 500)]
    [InlineData("16384", 16384)]
    [InlineData("0.5", 0.5)]
    public void ABareNumber_SetsBothThresholds(string raw, double expected)
    {
        Assert.True(JsEntryTrace.TryParseConfiguration(raw, out var turnMs, out var gapMs));
        Assert.Equal(expected, turnMs);
        Assert.Equal(expected, gapMs);
    }

    [Fact]
    public void NamedThresholds_AreSetIndependently()
    {
        Assert.True(JsEntryTrace.TryParseConfiguration("turn=100,gap=5000", out var turnMs, out var gapMs));
        Assert.Equal(100, turnMs);
        Assert.Equal(5000, gapMs);
    }

    // Naming one leaves the other at its default rather than at zero — "show me only long gaps"
    // must not also mean "show me every turn".
    [Fact]
    public void NamingOneThreshold_LeavesTheOtherDefaulted()
    {
        Assert.True(JsEntryTrace.TryParseConfiguration("gap=2000", out var turnMs, out var gapMs));
        Assert.Equal(DefaultTurnMs, turnMs);
        Assert.Equal(2000, gapMs);

        Assert.True(JsEntryTrace.TryParseConfiguration("turn=50", out turnMs, out gapMs));
        Assert.Equal(50, turnMs);
        Assert.Equal(DefaultGapMs, gapMs);
    }

    // `entry` is accepted for `turn` because the first name this had was "entry", and a reader
    // following an older note should not silently get the default.
    [Fact]
    public void EntryIsAcceptedAsAnAliasForTurn()
    {
        Assert.True(JsEntryTrace.TryParseConfiguration("entry=750", out var turnMs, out _));
        Assert.Equal(750, turnMs);
    }

    [Theory]
    [InlineData(" turn = 100 , gap = 5000 ")]
    [InlineData("TURN=100,GAP=5000")]
    public void SpacingAndCaseAreTolerated(string raw)
    {
        Assert.True(JsEntryTrace.TryParseConfiguration(raw, out var turnMs, out var gapMs));
        Assert.Equal(100, turnMs);
        Assert.Equal(5000, gapMs);
    }

    // The judgement call, pinned: an unrecognisable value enables the trace with the defaults. A
    // typo must not read as "off", because the reader would spend a whole reproduction before
    // learning that nothing was listening.
    [Theory]
    [InlineData("yes")]
    [InlineData("verbose")]
    [InlineData("turn")]
    [InlineData("turn=")]
    [InlineData("turn=abc")]
    [InlineData("=100")]
    public void AnUnrecognisableValue_StillEnablesTheDefaults(string raw)
    {
        Assert.True(JsEntryTrace.TryParseConfiguration(raw, out var turnMs, out var gapMs));
        Assert.Equal(DefaultTurnMs, turnMs);
        Assert.Equal(DefaultGapMs, gapMs);
    }

    // A recognised pair beside an unrecognised one keeps the pair.
    [Fact]
    public void AGoodPairSurvivesABadOneBesideIt()
    {
        Assert.True(JsEntryTrace.TryParseConfiguration("gap=abc,turn=42", out var turnMs, out var gapMs));
        Assert.Equal(42, turnMs);
        Assert.Equal(DefaultGapMs, gapMs);
    }

    // Parsed invariantly: a machine whose culture uses "," as the decimal separator must read
    // "0.5" as a half-millisecond, not fail and fall back to the default.
    [Fact]
    public void ANumberIsReadInvariantlyOfCulture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.True(JsEntryTrace.TryParseConfiguration("turn=0.5", out var turnMs, out _));
            Assert.Equal(0.5, turnMs);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }
}
