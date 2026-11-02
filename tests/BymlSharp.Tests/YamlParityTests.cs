namespace BymlSharp.Tests;

// The hand-written YAML writer and reader stand in for the YamlDotNet ones: same text out, same tree in.
public class YamlParityTests
{
    [Fact]
    public void WriterMatchesTheReferenceEmitter()
    {
        string[] strings =
        [
            "plain", "with space", "123", "", "a: b", "x #y", " lead", "trail ", "q'uote", "d\"q", "multi\nline", "über", "- dash",
            "yes", "~", "a,b", "[x]", "@at", "key:", "a\\b", "\u0001ctl", "\U0001F600", "0x10", "1e5", ".5", "1_000", "null", "True", "x/y.bgyml",
        ];
        Byml shared = Byml.Array([1f, 2f]);
        Byml root = Byml.Map(new Dictionary<string, Byml>
        {
            ["Strings"] = Byml.Array([.. strings.Select(s => (Byml)s)]),
            ["Flow"] = Byml.Map(new Dictionary<string, Byml> { ["a b"] = 1f, ["x,y"] = 2, ["Null"] = Byml.Null }),
            ["Shared"] = Byml.Array([shared, shared, Byml.HashMap(new Dictionary<ulong, Byml> { [1] = shared })]),
            ["Numbers"] = Byml.Array([float.NaN, float.NegativeInfinity, 1e20f, -0f, 5u, 5L, 5ul, 1.5, double.NaN, Byml.From([1, 2]), Byml.From([], 8)]),
            ["Nested"] = Byml.Array([Byml.Array([Byml.Array([1, "a"])]), Byml.Map(new Dictionary<string, Byml> { ["k"] = Byml.Array(["v"]) })]),
        });

        foreach (bool anchors in new[] { false, true })
            Assert.Equal(BymlYamlLegacy.Write(root, anchors), root.ToYaml(anchors));
    }

    [Theory]
    [InlineData("a: 1\nb: [1, 2, 3]  # trailing comment\n# whole-line comment\nc: {x: 1.5, y: -2}\n")]
    [InlineData("\"quoted key\": 'it''s'\nlist:\n- 1\n- - 2\n  - 3\n- k: v\n  k2: w\n")]
    [InlineData("root: &1\n  a: 1\nalias: *1\nseq: &2 [1.0, 2.0]\nagain: *2\n")]
    [InlineData("h: !h32\n  0x1: 1\n  0x2: x\nflow: !h64 {0x3: 3}\n")]
    [InlineData("n: 0x10\nm: -0x10\nf: 1e3\ni: .inf\nz: ~\nt: true\nw: 007\nb: !!binary AQID\n")]
    [InlineData("s: |\n  block\n  scalar\nafter: 1\n")]
    [InlineData("s: multi\n  line plain\n")]
    [InlineData("--- \na: 1\n")]
    [InlineData("a: 1\r\nb:\r\n- x\r\n- y\r\n")]
    [InlineData("")]
    [InlineData("just a string")]
    public void ReaderMatchesTheReferenceParser(string yaml)
    {
        Byml expected = BymlYamlLegacy.Read(yaml);
        Byml actual = Byml.FromYaml(yaml);

        Assert.Equal(BymlYamlLegacy.Write(expected, true), BymlYamlLegacy.Write(actual, true));
    }
}
