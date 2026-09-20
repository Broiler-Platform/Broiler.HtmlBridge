using Broiler.HtmlBridge;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// The <c>CSSRule</c> objects a page reads out of <c>cssRules</c> — which rules are there, what
/// <c>type</c> each answers, and the members each kind carries — asserted from page script, written before
/// the bridge's string-based rule builder was replaced by one that reads only the parsed
/// <c>Broiler.CSS</c> rule model.
/// <para>
/// <b>Why these were written first.</b> The bridge used to build a rule object two ways: from the parsed
/// model for the rule kinds <c>CssomRuleMetadata</c> names, and from the rule's <em>text</em> for
/// everything else, by matching a list of at-rule prefixes. Text that matched no prefix became a
/// <c>CSSStyleRule</c> whose selector was the at-rule's own prelude, so <c>@container</c>, <c>@scope</c>,
/// <c>@starting-style</c> and <c>@-webkit-keyframes</c> all reported <c>type</c> 1; and a prefix match
/// misfiled unrelated names (<c>@mediaeval</c> was an <c>@media</c> rule with the media text
/// <c>eval</c>). The same text path built every rule a page inserted through a nested
/// <c>cssRules.insertRule</c>. Most tests here failed against that builder; each says what it used to
/// answer.
/// </para>
/// <para>
/// <b>Two kinds of test live here, and the name says which.</b> A test without a prefix pins behaviour
/// that moved toward CSSOM and Chromium, and asserts their answer wherever the bridge can reach it; where
/// part of what it asserts still differs, its comment names that part and what Chromium does instead.
/// Several insert through the list-level <c>cssRules.insertRule</c> the bridge offers, which Chromium's
/// <c>CSSRuleList</c> does not have; their Chromium counterpart is the rule's own <c>insertRule</c>. The
/// tests named <c>Characterization_</c> pin what the bridge delivers where the whole answer is still known
/// to differ from Chromium, because reaching Chromium needs something the consumed <c>Broiler.CSS</c>
/// package does not have (a rule kind, a nesting representation) or is a separate piece of work
/// (rule-object identity, <c>CSSKeyframesRule.appendRule</c>). Each says what Chromium does instead.
/// </para>
/// <para>
/// The insertion half — <c>insertRule</c> at sheet and rule-list level, <c>deleteRule</c>, and
/// <c>cssText</c> round trips — is in <c>CssRuleObjectTests.Insertion.cs</c>.
/// </para>
/// </summary>
public partial class CssRuleObjectTests
{
    private const string PageUrl = "https://example.test/css-rules";

    /// <summary>
    /// Two author sheets, each read by its <c>id</c> through <c>sheetOf</c> so neither test depends on the
    /// other's position in <c>document.styleSheets</c>. <c>#members</c> holds the statement and descriptor
    /// at-rules whose members are compared; its <c>@import</c> is a <c>data:</c> URL so nothing is fetched,
    /// and its <c>@namespace</c> rules sit in their own sheet because a default namespace is per sheet.
    /// <c>#mapped</c> puts an at-rule CSSOM does not expose between two rules for <c>#mapped-probe</c>, so a
    /// CSSOM index and a model index differ and the cascade shows which rule an index reached.
    /// </summary>
    private const string PageHtml =
        "<!DOCTYPE html><html><head>" +
        "<style id=\"members\">@import url(\"data:text/css,p{}\") screen; " +
        "@namespace url(http://example.test/default); @namespace svg url(http://www.w3.org/2000/svg); " +
        "@font-face { font-family: Probe; } @page :first { margin-top: 1in; }</style>" +
        "<style id=\"mapped\">#mapped-probe { color: rgb(0, 0, 255); } @-ms-viewport { width: device-width; } " +
        "#mapped-probe { color: rgb(255, 0, 0); }</style>" +
        "</head><body><div id=\"mapped-probe\"></div><div id=\"out\"></div></body></html>";

    /// <summary>
    /// Page-global helpers the scripts below share, run as their own script ahead of the probe.
    /// <c>sheet</c> is a constructed sheet over some text, which is what most tests read because it needs
    /// no markup; <c>sheetOf</c> finds an element-backed sheet by its owner's <c>id</c>;
    /// <c>describeRule</c> spells a rule as its type, its selector (<c>-</c> when it has none) and its
    /// media text when it has one, and names a missing rule rather than throwing on it.
    /// </summary>
    private const string Helpers = """
        function sheet(text) {
          var s = new CSSStyleSheet();
          s.replaceSync(text);
          return s;
        }
        function sheetOf(id) {
          var list = document.styleSheets;
          for (var i = 0; i < list.length; i++) {
            var s = list[i] || list.item(i);
            if (s && s.ownerNode && s.ownerNode.id === id) return s;
          }
          return null;
        }
        function describeRule(r) {
          if (r === null) return 'null';
          if (r === undefined) return 'undefined';
          return r.type + ':' + (r.selectorText === undefined ? '-' : r.selectorText) +
                 (r.media !== undefined ? '/media=' + r.media : '');
        }
        """;

    /// <summary>
    /// A throw is written to <c>#out</c> too, as <see cref="NodeRelationshipCanonicalTests"/> does:
    /// several members below are absent on one side of the change, and a script that died on one would
    /// otherwise leave <c>#out</c> empty with no word of where. Decoded, because the answers quote CSS
    /// strings and the serializer writes '"' in text as an entity.
    /// </summary>
    private static string Run(string script) =>
        PageProbe.RunGuarded(PageHtml, PageUrl, Helpers, script, decode: true);

    // ── Which rules are in cssRules ───────────────────────────────────────────────────────────────

    [Fact]
    public void UnknownAtRulesAndCharsetAreNotInCssRulesAndIndicesSkipThem()
    {
        // CSSOM "parse a CSS rule" produces no rule for an at-rule the UA does not support or for one
        // missing the block its grammar requires, and CSS Syntax 3 §8.2 says @charset is not a rule at
        // all, so Chromium's cssRules holds .a and .b and nothing else. This used to answer
        //   len=7 2:- 1:.a 1:- 4:-/media=print 4:-/media=eval 1:@-ms-viewport 1:.b
        // — a CSSCharsetRule, '@foo bar;' as a type-1 rule with no selector, a blockless @media rule,
        // '@mediaeval' prefix-matched
        // into an @media rule with media 'eval', and '@-ms-viewport' as a style rule selecting itself —
        // and item(1) was .a and deleteRule(1) removed it, because indices were positions in the parsed
        // model. The hidden
        // rules stay in that model (see the element-sheet test below); only the CSSOM view skips them.
        Assert.Equal(
            "len=2 1:.a 1:.b item1=1:.b item2=null afterDelete=len=1 1:.a",
            Run("""
                (function () {
                  var s = sheet('@charset "utf-8"; .a { color: red; } @foo bar; @media print; @mediaeval { } ' +
                                '@-ms-viewport { width: device-width; } .b { color: blue; }');
                  function describe() {
                    var r = s.cssRules, parts = ['len=' + r.length];
                    for (var i = 0; i < r.length; i++) parts.push(describeRule(r[i]));
                    return parts.join(' ');
                  }
                  var before = describe();
                  var items = ' item1=' + describeRule(s.cssRules.item(1)) + ' item2=' + describeRule(s.cssRules.item(2));
                  s.deleteRule(1);
                  return before + items + ' afterDelete=' + describe();
                })()
                """));
    }

    [Fact]
    public void AnElementSheetsInsertAndDeleteIndicesSkipHiddenRulesInTheCascadeToo()
    {
        // The <style>-backed sheet has its own index sync, and its rules are the list the renderer and
        // getComputedStyle read, so this is where an index that means the wrong rule shows up as the wrong
        // color. Index 2 is the end of Chromium's two-rule list, so the inserted green rule lands last and
        // wins; deleteRule(1) removes the red rule. This used to answer
        //   before=rgb(255, 0, 0) len=3 insertedAt=2 afterInsert=rgb(255, 0, 0) afterDelete=rgb(255, 0, 0)
        //   len=3 kept=rgb(0, 0, 255),rgb(255, 0, 0)
        // — index 2 was a model index, so the insert landed ahead of the red rule, and deleteRule(1) removed
        // the hidden @-ms-viewport rule instead of a rule the page could see. The hidden rule is still in
        // the list the cascade reads; only the indices skip it.
        Assert.Equal(
            "before=rgb(255, 0, 0) len=2 insertedAt=2 afterInsert=rgb(0, 128, 0) afterDelete=rgb(0, 128, 0) " +
            "len=2 kept=rgb(0, 0, 255),rgb(0, 128, 0)",
            Run("""
                (function () {
                  var s = sheetOf('mapped'), p = document.getElementById('mapped-probe');
                  function color() { return getComputedStyle(p).color; }
                  var before = color(), len = s.cssRules.length;
                  var at = s.insertRule('#mapped-probe { color: rgb(0, 128, 0); }', 2);
                  var afterInsert = color();
                  s.deleteRule(1);
                  var r = s.cssRules;
                  return 'before=' + before + ' len=' + len + ' insertedAt=' + at + ' afterInsert=' + afterInsert +
                         ' afterDelete=' + color() + ' len=' + r.length +
                         ' kept=' + r[0].style.color + ',' + r[r.length - 1].style.color;
                })()
                """));
    }

    // ── Rule kinds the legacy constants do not name ───────────────────────────────────────────────

    [Fact]
    public void ContainerScopeAndStartingStyleAreGroupingRulesWithTheirOwnRuleLists()
    {
        // Chromium exposes CSSContainerRule, CSSScopeRule and CSSStartingStyleRule as grouping rules: type
        // 0 (the CSSOM getter's answer for every rule without a legacy constant), a cssRules list of their
        // children, and conditionText on @container, which includes the container name. The consumed
        // Broiler.CSS has no CssomRuleType member for any of them, so the bridge recognises them by name.
        // This used to answer
        //   1|@container card (min-width: 100px)|noList|undefined 1|@scope (.x)|noList|undefined
        //   1|@starting-style|noList|undefined noList 3 scope=@scope (.x) {  } starting=@starting-style {  }
        // — each was a CSSStyleRule whose selector was its own prelude, with no children and so no child
        // text in its cssText. Chromium inserts
        // through c.insertRule on the rule itself; CSSRuleList has no insertRule there, and moving it onto
        // the rule is outside this change.
        Assert.Equal(
            "0|noSelector|1:.a|card (min-width: 100px) 0|noSelector|1:.b|undefined 0|noSelector|1:.c|undefined " +
            "1:.k:true:true 3 scope=@scope (.x) { .b { color: blue; } } starting=@starting-style { .c { opacity: 0; } }",
            Run("""
                (function () {
                  var s = sheet('@container card (min-width: 100px) { .a { color: red; } } ' +
                                '@scope (.x) { .b { color: blue; } } @starting-style { .c { opacity: 0; } }');
                  function d(r) {
                    return r.type + '|' + (r.selectorText === undefined ? 'noSelector' : r.selectorText) + '|' +
                           (r.cssRules === undefined ? 'noList' : r.cssRules.length + ':' + r.cssRules[0].selectorText) +
                           '|' + r.conditionText;
                  }
                  var rules = s.cssRules, described = [d(rules[0]), d(rules[1]), d(rules[2])], c = rules[0];
                  var texts = ' scope=' + rules[1].cssText + ' starting=' + rules[2].cssText;
                  var inserted = c.cssRules === undefined ? 'noList'
                    : c.cssRules.insertRule('.k { color: green }', 1) + ':' + c.cssRules[1].selectorText + ':' +
                      (c.cssRules[1].parentRule === c) + ':' + (c.cssRules[1].parentStyleSheet === s);
                  return described.join(' ') + ' ' + inserted + ' ' + s.cssRules.length + texts;
                })()
                """));
    }

    [Fact]
    public void VendorPrefixedKeyframesIsAKeyframesRule()
    {
        // Chromium parses @-webkit-keyframes as an alias of @keyframes: a CSSKeyframesRule (type 7) with
        // its name and its keyframes, whose cssText keeps the prefix. This used to answer
        //   1|undefined|noList|-|@-webkit-keyframes spin {  }
        // — a style rule selecting '@-webkit-keyframes spin', which is what a page's animation-library
        // feature check found. Chromium's cssText is multi-line and writes the keys as 0% and 100%; that
        // format is not asserted here.
        Assert.Equal(
            "7|spin|2|from,to|@-webkit-keyframes spin { from { color: red; } to { color: blue; } }",
            Run("""
                (function () {
                  var r = sheet('@-webkit-keyframes spin { from { color: red; } to { color: blue; } }').cssRules[0];
                  var list = r.cssRules;
                  return r.type + '|' + r.name + '|' + (list === undefined ? 'noList' : list.length) + '|' +
                         (list === undefined ? '-' : list[0].keyText + ',' + list[1].keyText) + '|' + r.cssText;
                })()
                """));
    }

    [Fact]
    public void AtRulesShippedWithoutALegacyTypeAreOpaqueRules()
    {
        // @position-try, @view-transition and @font-palette-values are in Chromium's cssRules with type 0,
        // and @font-feature-values with FONT_FEATURE_VALUES_RULE (14, css-fonts-4). The bridge gives them
        // their type and cssText, and @position-try its style: the descriptor block, the one member of
        // these that worked before and that Chromium has too (top=0px). Chromium has no style on the other
        // three. Their own members (CSSPositionTryRule.name, CSSViewTransitionRule.navigation,
        // CSSFontPaletteValuesRule.fontFamily, the feature-value maps) are a remaining gap. This used to
        // answer
        //   len=4 types=1,1,1,1 selectors=@position-try --p,@view-transition,@font-palette-values --pal,
        //   @font-feature-values Probe styles=object,object,object,object texts=... /
        //   @font-feature-values Probe {  }
        // — every one a style rule selecting its own prelude, whose cssText kept a declaration block and
        // lost a rule block.
        Assert.Equal(
            "len=4 types=0,0,0,14 selectors=undefined,undefined,undefined,undefined " +
            "styles=object,undefined,undefined,undefined top=0px texts=@position-try --p { top: 0px; } / " +
            "@view-transition { navigation: auto; } / @font-palette-values --pal { font-family: Probe; } / " +
            "@font-feature-values Probe { @styleset { nice: 1; } }",
            Run("""
                (function () {
                  var r = sheet('@position-try --p { top: 0px; } @view-transition { navigation: auto; } ' +
                                '@font-palette-values --pal { font-family: Probe; } ' +
                                '@font-feature-values Probe { @styleset { nice: 1; } }').cssRules;
                  var types = [], selectors = [], styles = [], texts = [];
                  for (var i = 0; i < r.length; i++) {
                    types.push(r[i].type);
                    selectors.push(String(r[i].selectorText));
                    styles.push(typeof r[i].style);
                    texts.push(r[i].cssText);
                  }
                  return 'len=' + r.length + ' types=' + types.join(',') + ' selectors=' + selectors.join(',') +
                         ' styles=' + styles.join(',') + ' top=' + r[0].style.getPropertyValue('top') +
                         ' texts=' + texts.join(' / ');
                })()
                """));
    }

    [Fact]
    public void RuleTypeIsTheCssomConstantOrZero()
    {
        // CSSOM's type getter answers NAMESPACE_RULE 10; css-counter-styles-3 defines COUNTER_STYLE_RULE 11
        // and css-conditional-3 SUPPORTS_RULE 12; @layer (block and statement) and @property have no
        // constant and answer 0 in Chromium. This used to answer 9,10,11,12,12,25,4,1 — the consumed
        // CssomRuleType's own numbering, under which a page testing type === 12 for @supports found
        // @layer instead. The enum is unchanged; the bridge maps it to the IDL constants.
        Assert.Equal(
            "10,11,12,0,0,0,4,1",
            Run("""
                (function () {
                  var r = sheet('@namespace svg url(http://www.w3.org/2000/svg); ' +
                                '@counter-style c { system: cyclic; symbols: x; } @supports (color: red) { } ' +
                                '@layer l { } @layer m; @property --p { syntax: "*"; inherits: true; } ' +
                                '@media print { } .a { color: red; }').cssRules;
                  var types = [];
                  for (var i = 0; i < r.length; i++) types.push(r[i].type);
                  return types.join(',');
                })()
                """));
    }

    [Fact]
    public void AtRuleMembersAreTheSameFromSheetTextAndFromANestedInsert()
    {
        // The statement and descriptor at-rules a page reads members off, from <style> text and again
        // after inserting each one's cssText into an @media rule's list. The nested copy used to come from
        // the text builder and now comes from the same parsed-model builder as the sheet's own; the
        // members already agreed for these well-formed inputs, and still do. The namespace rules used to
        // answer type 9 (see RuleTypeIsTheCssomConstantOrZero). Two remaining gaps are asserted here: the
        // default namespace's prefix is undefined where Chromium answers '', and the nested half inserts
        // @import and @namespace into a grouping rule, which Chromium's CSSGroupingRule.insertRule (and
        // CSSOM's "insert a CSS rule") rejects with a HierarchyRequestError. Chromium also writes
        // @namespace cssText with url("..."), which is not asserted.
        Assert.Equal(
            "3|data:text/css,p{}|screen|@import url(\"data:text/css,p{}\") screen; " +
            "10|undefined|http://example.test/default 10|svg|http://www.w3.org/2000/svg " +
            "5|Probe|@font-face { font-family: Probe; } 6|:first|1in|@page :first { margin-top: 1in; } nested=same",
            Run("""
                (function () {
                  function desc(x) {
                    switch (x.type) {
                      case 3: return x.type + '|' + x.href + '|' + x.media + '|' + x.cssText;
                      case 5: return x.type + '|' + x.style.getPropertyValue('font-family') + '|' + x.cssText;
                      case 6: return x.type + '|' + x.selectorText + '|' + x.style.getPropertyValue('margin-top') +
                                     '|' + x.cssText;
                      default: return x.type + '|' + String(x.prefix) + '|' + x.namespaceURI;
                    }
                  }
                  var r = sheetOf('members').cssRules, own = [];
                  for (var i = 0; i < r.length; i++) own.push(desc(r[i]));
                  var m = sheet('@media screen { }').cssRules[0], nested = 'same';
                  for (var j = 0; j < r.length; j++) m.cssRules.insertRule(r[j].cssText, m.cssRules.length);
                  for (var k = 0; k < r.length; k++) {
                    var copy = desc(m.cssRules[k]);
                    if (copy !== own[k] && nested === 'same') nested = own[k] + ' vs ' + copy;
                  }
                  return own.join(' ') + ' nested=' + nested;
                })()
                """));
    }
}
