using System.Text;
using Mogu.Core.Torrents;

namespace Mogu.Tests.Torrents;

[TestClass]
public sealed class BencodeTests
{
    private static byte[] Ascii(string value) => Encoding.ASCII.GetBytes(value);

    [TestMethod]
    public void DecodesNestedValues()
    {
        var value = (BencodeDictionary)Bencode.Decode(Ascii("d3:bar4:spam3:fooi42e4:listli-3e0:ee"));

        CollectionAssert.AreEqual(Ascii("spam"), (byte[])value["bar"]);
        Assert.AreEqual(42L, value["foo"]);
        var list = (List<object>)value["list"];
        Assert.AreEqual(-3L, list[0]);
        Assert.IsEmpty((byte[])list[1]);
    }

    [TestMethod]
    public void RoundTripsAndSortsKeysBytewise()
    {
        var dictionary = new BencodeDictionary
        {
            ["zeta"] = 1L,
            ["b"] = new List<object> { Ascii("x"), 2L, new BencodeDictionary { ["k"] = "v" } },
            ["a"] = Ascii("hello"),
            ["\u00e9"] = 3L, // sorts after ASCII by UTF-8 bytes
            ["A"] = 4L,
        };

        var encoded = Bencode.Encode(dictionary);

        Assert.AreEqual("d1:Ai4e1:a5:hello1:bl1:xi2ed1:k1:vee4:zetai1e2:\u00c3\u00a9i3ee", Encoding.Latin1.GetString(encoded));
        CollectionAssert.AreEqual(encoded, Bencode.Encode(Bencode.Decode(encoded)));
    }

    [TestMethod]
    public void ByteStringsStayBinary()
    {
        byte[] binary = [0x00, 0xFF, 0xFE, 0x80, (byte)'e'];
        var encoded = Bencode.Encode(new List<object> { binary });

        var decoded = (List<object>)Bencode.Decode(encoded);

        CollectionAssert.AreEqual(binary, (byte[])decoded[0]);
    }

    [TestMethod]
    [DataRow("i-0e", 0L)]
    [DataRow("i03e", 3L)]
    [DataRow("i-42e", -42L)]
    [DataRow("i9223372036854775807e", long.MaxValue)]
    public void DecodesIntegersLikeTheOldDecoder(string input, long expected)
    {
        Assert.AreEqual(expected, Bencode.Decode(Ascii(input)));
    }

    [TestMethod]
    [DataRow("", DisplayName = "empty")]
    [DataRow("e", DisplayName = "bare end")]
    [DataRow("i12", DisplayName = "unterminated int")]
    [DataRow("ie", DisplayName = "empty int")]
    [DataRow("i-e", DisplayName = "minus only")]
    [DataRow("i1-2e", DisplayName = "minus inside int")]
    [DataRow("i1x2e", DisplayName = "letter inside int")]
    [DataRow("i99999999999999999999e", DisplayName = "int overflow")]
    [DataRow("5:abc", DisplayName = "short string")]
    [DataRow("3x:abc", DisplayName = "bad string length")]
    [DataRow("12", DisplayName = "unterminated string length")]
    [DataRow("l", DisplayName = "unterminated list")]
    [DataRow("d3:fooe", DisplayName = "uneven dict")]
    [DataRow("di1ei2ee", DisplayName = "integer key")]
    [DataRow("x", DisplayName = "unknown type")]
    [DataRow("i1ei2e", DisplayName = "trailing data")]
    public void RejectsMalformedInput(string input)
    {
        Assert.ThrowsExactly<BencodeException>(() => Bencode.Decode(Ascii(input)));
    }

    [TestMethod]
    public void RejectsInvalidUtf8Keys()
    {
        byte[] input = [(byte)'d', (byte)'1', (byte)':', 0xFF, (byte)'i', (byte)'1', (byte)'e', (byte)'e'];

        Assert.ThrowsExactly<BencodeException>(() => Bencode.Decode(input));
    }

    [TestMethod]
    public void RejectsHostileNesting()
    {
        var input = Ascii(new string('l', 100_000) + new string('e', 100_000));

        Assert.ThrowsExactly<BencodeException>(() => Bencode.Decode(input));
    }

    [TestMethod]
    public void TrailingDataCanBeAllowed()
    {
        Assert.AreEqual(1L, Bencode.Decode(Ascii("i1e\n"), allowTrailingData: true));
    }

    [TestMethod]
    public void ErrorsMentionThePosition()
    {
        var error = Assert.ThrowsExactly<BencodeException>(() => Bencode.Decode(Ascii("li1ei2x")));

        Assert.Contains("position 6", error.Message);
    }

    [TestMethod]
    public void EncodeRejectsUnsupportedTypes()
    {
        Assert.ThrowsExactly<BencodeException>(() => Bencode.Encode(1.5));
    }
}
