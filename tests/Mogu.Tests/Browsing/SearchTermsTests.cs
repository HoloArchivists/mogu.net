using Mogu.Web.Services;

namespace Mogu.Tests.Browsing;

[TestClass]
public sealed class SearchTermsTests
{
    private static string Describe(string query) => string.Join(
        " ; ",
        SearchTerms.Parse(query).Select(g => (g.Exclude ? "-" : "") + string.Join("|", g.Alternatives.Select(a => $"[{a}]"))));

    [TestMethod]
    [DataRow("", "")]
    [DataRow("   ", "")]
    [DataRow("foo bar", "[foo] ; [bar]")]
    [DataRow("  foo   bar  ", "[foo] ; [bar]")]
    [DataRow("\"foo bar\" baz", "[foo bar] ; [baz]")]
    [DataRow("foo -bar", "[foo] ; -[bar]")]
    [DataRow("-\"foo bar\"", "-[foo bar]")]
    [DataRow("foo|bar", "[foo]|[bar]")]
    [DataRow("\"foo bar\"|\"foo baz\"", "[foo bar]|[foo baz]")]
    [DataRow("-\"foo bar\"|baz", "-[foo bar]|[baz]")]
    [DataRow("(hello world) x", "[hello] ; [world]")]
    [DataRow("a bc d", "[bc]")]
    [DataRow("- -a", "")]
    [DataRow("a|bc", "[bc]")]
    [DataRow("\"unterminated phrase", "[unterminated phrase]")]
    [DataRow("foo-bar", "[foo-bar]")]
    [DataRow("\"(kept)\"", "[(kept)]")]
    public void Parse(string query, string expected) => Assert.AreEqual(expected, Describe(query));

    [TestMethod]
    public void EscapeLikeEscapesWildcardsAndEscapeCharacter() =>
        Assert.AreEqual(@"100\% a\_b c\\d", SearchTerms.EscapeLike(@"100% a_b c\d"));
}
