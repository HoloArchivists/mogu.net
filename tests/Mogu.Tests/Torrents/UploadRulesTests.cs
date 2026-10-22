using Mogu.Core.Torrents;

namespace Mogu.Tests.Torrents;

[TestClass]
public sealed class UploadRulesTests
{
    [TestMethod]
    public void SanitizeReplacesXmlIllegalCharacters()
    {
        Assert.AreEqual("a\uFFFDb\tc\nd\re\uFFFD\uFFFD", UploadRules.Sanitize("a\u0001b\tc\nd\re\u001f\uFFFE"));
    }

    [TestMethod]
    public void SanitizeKeepsSurrogatePairsButNotLoneSurrogates()
    {
        Assert.AreEqual("\U0001F363 ok", UploadRules.Sanitize("\U0001F363 ok"));
        Assert.AreEqual("x\uFFFDy\uFFFD", UploadRules.Sanitize("x\uD800y\uDC00"));
    }

    [TestMethod]
    [DataRow("con")]
    [DataRow("CON.txt")]
    [DataRow("lpt1.mkv")]
    [DataRow("evil\u202Etxt.exe")]
    public void RejectsForbiddenNames(string name)
    {
        Assert.IsFalse(UploadRules.AreFilenamesAllowed(["fine", name]));
    }

    [TestMethod]
    [DataRow("con.tar.gz")]
    [DataRow("console.txt")]
    [DataRow("Show - 01.mkv")]
    public void AllowsOrdinaryNames(string name)
    {
        Assert.IsTrue(UploadRules.AreFilenamesAllowed([name]));
    }

    [TestMethod]
    [DataRow("My cool movie.mov", "My_cool_movie.mov")]
    [DataRow("../../../etc/passwd", "etc_passwd")]
    [DataRow("[Group] \u00e9pisode 01.torrent", "Group_episode_01.torrent")]
    [DataRow("\u3042\u3042.torrent", "torrent")]
    public void SecureFilenameLikeWerkzeug(string input, string expected)
    {
        Assert.AreEqual(expected, UploadRules.SecureFilename(input));
    }

    [TestMethod]
    public void QuoteLikePython()
    {
        Assert.AreEqual("a%20b/c%3F~_.-%C3%A9", UploadRules.Quote("a b/c?~_.-\u00e9"));
        Assert.AreEqual("a%2Fb", UploadRules.Quote("a/b", safe: ""));
    }

    [TestMethod]
    public void BaseNameStripsClientPaths()
    {
        Assert.AreEqual("file.torrent", UploadRules.BaseName(@"C:\Users\me\file.torrent"));
        Assert.AreEqual("file.torrent", UploadRules.BaseName("file.torrent"));
    }
}
