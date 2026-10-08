namespace BymlSharp.Tests;

public class YamlTests
{
    [Fact]
    public void EveryTypeRoundTrips()
    {
        Byml root = Byml.Map(new Dictionary<string, Byml>
        {
            ["Bool"] = true,
            ["Int"] = -5,
            ["UInt"] = 0xFFFFFFFFu,
            ["Int64"] = long.MinValue,
            ["UInt64"] = ulong.MaxValue,
            ["Float"] = 0.1f,
            ["Double"] = 0.1,
            ["String"] = "text",
            ["Null"] = Byml.Null,
            ["Binary"] = Byml.From([1, 2, 3]),
            ["Aligned"] = Byml.From([4, 5, 6], 0x1000),
            ["Array"] = Byml.Array([1, "two", Byml.Array([3f])]),
            ["Hash32"] = Byml.HashMap(new Dictionary<ulong, Byml> { [0xDEADBEEF] = 1 }, wide: false),
            ["Hash64"] = Byml.HashMap(new Dictionary<ulong, Byml> { [ulong.MaxValue] = "x" }),
        });

        Byml back = Byml.FromYaml(root.ToYaml());

        Assert.Equal(root.ToBinary(), back.ToBinary());
        Assert.Equal(root.ToYaml(), back.ToYaml());
    }

    [Theory]
    [InlineData(0x00000000u)]
    [InlineData(0x80000000u)]
    [InlineData(0x00000001u)]
    [InlineData(0x7F7FFFFFu)]
    [InlineData(0x501502F9u)]
    [InlineData(0x7FC00000u)]
    [InlineData(0xFF800000u)]
    [InlineData(0xFFC00000u)]
    [InlineData(0x7FC00001u)]
    public void FloatsKeepTheirBits(uint bits)
    {
        float value = BitConverter.UInt32BitsToSingle(bits);
        Byml back = Byml.FromYaml(Byml.From(value).ToYaml());
        Assert.Equal(BymlType.Float, back.Type);
        Assert.Equal(BitConverter.SingleToUInt32Bits(value), BitConverter.SingleToUInt32Bits(back.Float));
    }

    [Theory]
    [InlineData(0x7FF8000000000000ul)]
    [InlineData(0xFFF8000000000000ul)]
    [InlineData(0x8000000000000000ul)]
    public void DoublesKeepTheirBits(ulong bits)
    {
        Byml back = Byml.FromYaml(Byml.From(BitConverter.UInt64BitsToDouble(bits)).ToYaml());
        Assert.Equal(BymlType.Double, back.Type);
        Assert.Equal(bits, BitConverter.DoubleToUInt64Bits(back.Double));
    }

    [Theory]
    [InlineData("123")]
    [InlineData("1.5")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("0xFF")]
    [InlineData("")]
    [InlineData(".inf")]
    public void StringsThatLookLikeOtherTypesStayStrings(string value)
    {
        Byml back = Byml.FromYaml(Byml.From(value).ToYaml());
        Assert.Equal(BymlType.String, back.Type);
        Assert.Equal(value, back.String);
    }

    [Fact]
    public void SharedNodesBecomeAnchors()
    {
        Byml translate = Byml.Array([1f, 2f, 3f]);
        Byml hash = Byml.From(0x1234_5678_9ABC_DEF0ul);
        Byml root = Byml.Map(new Dictionary<string, Byml>
        {
            ["Hash"] = hash,
            ["Translate"] = translate,
            ["Phive"] = Byml.Map(new Dictionary<string, Byml> { ["ID"] = hash, ["Translate"] = translate }),
        });

        string yaml = root.ToYaml();
        Byml back = Byml.FromYaml(yaml);

        Assert.Contains("&1", yaml);
        Assert.Contains("*1", yaml);
        Assert.Same(back["Hash"], back["Phive"]!["ID"]);
        Assert.Same(back["Translate"], back["Phive"]!["Translate"]);
        Assert.Equal(root.ToBinary(), back.ToBinary());
        Assert.Equal(yaml, back.ToYaml());
    }

    [Fact]
    public void SharedValuesAreWrittenAtEachUseAndPointAtTheLast()
    {
        Byml hash = Byml.From(7L);
        byte[] binary = Byml.Array([hash, Byml.From(8L), hash]).ToBinary();

        BymlFile file = BymlFile.FromBinary(binary);

        Assert.Equal(3, Enumerable.Range(0, binary.Length / 8).Count(i => BitConverter.ToInt64(binary, i * 8) is 7 or 8));
        Assert.Same(file.Root[0], file.Root[2]);
        Assert.Equal(binary, file.Write());
    }

    [Fact]
    public void ContainersOnlyShareWhenTheyAreOneNode()
    {
        Byml Vector() => Byml.Array([1f, 2f, 3f]);
        Byml shared = Vector();

        int separate = Byml.Array([Vector(), Vector()]).ToBinary().Length;
        int once = Byml.Array([shared, shared]).ToBinary().Length;
        int byContent = new BymlFile(Byml.Array([Vector(), Vector()])) { ShareIdenticalContainers = true }.Write().Length;

        Assert.True(once < separate);
        Assert.Equal(once, byContent);
    }

    [Fact]
    public void SlIsAnAliasOfL()
    {
        Byml sl = Byml.FromYaml("!sl -12");
        Assert.Equal(BymlType.Int64, sl.Type);
        Assert.Equal(Byml.FromYaml("!l -12").Int64, sl.Int64);
    }

    [Theory]
    [InlineData("!l 0xFFFFFFFFFFFFFFFF", -1L)]
    [InlineData("!sl 0x8000000000000000", long.MinValue)]
    [InlineData("!l 0x7FFFFFFFFFFFFFFF", long.MaxValue)]
    [InlineData("!l -0x10", -16L)]
    public void HexInt64IsTwosComplement(string yaml, long expected)
    {
        Byml node = Byml.FromYaml(yaml);
        Assert.Equal(BymlType.Int64, node.Type);
        Assert.Equal(expected, node.Int64);
    }

    [Theory]
    [InlineData("0xFFFFFFFF", -1)]
    [InlineData("0x80000000", int.MinValue)]
    [InlineData("0x7FFFFFFF", int.MaxValue)]
    public void HexIntIsTwosComplement(string yaml, int expected)
    {
        Byml node = Byml.FromYaml(yaml);
        Assert.Equal(BymlType.Int, node.Type);
        Assert.Equal(expected, node.Int);
    }

    [Fact]
    public void HexWiderThanAnIntIsInt64()
    {
        Byml node = Byml.FromYaml("0xFFFFFFFFFFFFFFFF");
        Assert.Equal(BymlType.Int64, node.Type);
        Assert.Equal(-1L, node.Int64);
    }

    [Fact]
    public void UnsignedTagsStayUnsigned()
    {
        Assert.Equal(0xFFFFFFFFu, Byml.FromYaml("!u 0xFFFFFFFF").UInt);
        Assert.Equal(ulong.MaxValue, Byml.FromYaml("!ul 0xFFFFFFFFFFFFFFFF").UInt64);
    }

    [Fact]
    public void ReadsOeadStyleDocuments()
    {
        Byml root = Byml.FromYaml("""
            Actors:
            - Name: Obj_Example
              Scale: {X: 1.0, Y: 2.5, Z: 1.0}
              HashId: !u 0x1234
              Big: !ul 0x100000000
              Data: !!binary AQID
            """);

        Byml actor = root["Actors"]![0]!;
        Assert.Equal("Obj_Example", actor["Name"]!.String);
        Assert.Equal(2.5f, actor["Scale"]!["Y"]!.Float);
        Assert.Equal(0x1234u, actor["HashId"]!.UInt);
        Assert.Equal(0x100000000ul, actor["Big"]!.UInt64);
        Assert.Equal([1, 2, 3], actor["Data"]!.Binary);
    }
}
