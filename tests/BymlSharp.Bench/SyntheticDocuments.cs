using BymlSharp;

internal static class SyntheticDocuments
{
    private static Byml M(params (string, Byml)[] e) => Byml.Map(e.ToDictionary(x => x.Item1, x => x.Item2));

    public static void Print()
    {
        foreach (var (name, root) in Roots())
        {
            Console.WriteLine($"##### {name}");
            Console.WriteLine(root.ToYaml(anchors: true));
        }
    }

    public static IEnumerable<(string, Byml)> Roots()
    {
        Byml shared = Byml.Array([1f, 2f]);
        Byml big = Byml.From(0x1234_5678_9ABC_DEF0ul);
        Byml bm = M(("a", 1), ("b", Byml.Array([1, 2, 3, 4, 5, 6, 7, 8, 9])));
        Byml bs = Byml.Array([1, 2, 3, 4, 5, 6, 7, 8, 9]);
        Byml bh = Byml.HashMap(new Dictionary<ulong, Byml> { [1] = 1, [2] = bs });
        Byml fh = Byml.HashMap(new Dictionary<ulong, Byml> { [1] = 1, [2] = big, [3] = big }, wide: false);

        yield return ("kitchen", M(
            ("Empty", Byml.Map()), ("EmptyA", Byml.Array()), ("Null", Byml.Null),
            ("Flow", M(("X", 1f), ("Y", 2.5f), ("Z", -3))),
            ("FlowA", Byml.Array([1, 2, 3])),
            ("Nine", bs),
            ("NestA", Byml.Array([Byml.Array([1, "a"]), Byml.Array([Byml.Array([1, "a"]), M(("k", "v"))])])),
            ("Nums", Byml.Array([0, -1, int.MaxValue, int.MinValue, 1.0f, 0.1f, 1e20f, 1e-7f, float.NaN, float.PositiveInfinity, float.NegativeInfinity, 123456.789f, 3.4028235E+38f, -0.0f, 1e-45f])),
            ("Tags", Byml.Array([5u, 5L, 5ul, 1.5, 1e30, double.NaN, double.NegativeInfinity, long.MinValue, ulong.MaxValue, Byml.From(new byte[] { 1, 2, 3 }), Byml.From(new byte[] { 1, 2, 3 }, 16), Byml.From(Array.Empty<byte>()), true, false])),
            ("Anch", Byml.Array([shared, shared, big, big, Byml.Array([shared])])),
            ("H32", Byml.HashMap(new Dictionary<ulong, Byml> { [0xDEADBEEF] = 1, [3] = "x" }, wide: false)),
            ("H64", Byml.HashMap(new Dictionary<ulong, Byml> { [ulong.MaxValue] = Byml.Array([1, 2]), [1] = M(("a", Byml.Array([M(("z", 1), ("y", "s"))]))) })),
            ("a b", 1), ("x,y", 2), ("k:", 3), ("", 4), ("123", 5)));

        string[] strings =
        [
            "plain", "with space", "123", "", "a: b", "x #y", " lead", "trail ", "q'uote", "d\"q", "multi\nline", "tab\there",
            "über", "- dash", "yes", "no", "~", "a,b", "[x]", "{x}", "@at", "*star", "&amp", "!bang", "%pct", "key:", "a\\b",
            "\u0001ctl", "😀", "0x10", "1e5", ".5", "1_000", "-", "?", ":", "null", "Null", "true", "False", "NaN", ".nan", ".inf",
            "-.inf", "+5", "-5", "5.", "1.5.2", "12abc", "a  b", "a - b", "x/y/z.bgyml", "Obj_Name_01", "---", "...", "a\rb", "#c",
            "a#b", "a #b", "'q'", "\"q\"", "|", ">", "=", "<<", "é", "日本語", new string('k', 200), "0o17", "0b1", "1,2", "1 2", "2024-05-06",
            "12:30", "http://x.y/z", "a:b", "a: ", " ", "  ", "\t", "\\", "\"", "'",
        ];
        yield return ("strings-values", Byml.Array([.. strings.Select(s => (Byml)s)]));
        yield return ("strings-nested", M(("List", Byml.Array([.. strings.Where(s => !s.Contains('\n') && s.Length < 100).Select(s => (Byml)s)]))));
        foreach (string s in strings.Where(s => s.Length < 100))
            yield return ($"key[{s.Replace("\n", "\\n")}]", M((s, 1), ("z", Byml.Array([1, 2])), ("flow", M((s, 2), ("y", s))), ("blk", M((s, Byml.Array([s, s]))))));
        yield return ("strings-hash", Byml.HashMap(new Dictionary<ulong, Byml> { [1] = "x y", [2] = "a: b" }));

        yield return ("anchors-block", M(("m1", bm), ("m2", bm), ("s1", bs), ("s2", bs), ("h1", bh), ("h2", bh),
            ("seq", Byml.Array([bm, bm, bs, bs, bh, bh, Byml.Array([bm, bs]), M(("q", bm), ("r", bs))])), ("fh", fh), ("fh2", fh)));
        yield return ("anchors-first-in-seq", Byml.Array([bm, bm, Byml.Array([bs, bs]), M(("a", bh), ("b", bh))]));
        yield return ("anchors-first-in-seq2", Byml.Array([bs, M(("a", bs))]));
        yield return ("anchors-first-in-seq3", Byml.Array([bh, bh]));
        yield return ("seqhash", Byml.Array([bh, M(("x", 1)), Byml.Array([M(("y", bh))])]));
        yield return ("root-hash", bh);
        yield return ("root-flow-hash", fh);
        yield return ("root-seq", Byml.Array([1, 2]));
        yield return ("root-seq-block", bs);
        yield return ("root-empty-seq", Byml.Array());
        yield return ("root-empty-map", Byml.Map());
        yield return ("root-null", Byml.Null);
        yield return ("root-str", "hello");
        yield return ("root-int", 5);
        yield return ("root-long", 5L);
        yield return ("root-seq-nested", Byml.Array(["x", Byml.Array([1, 2, 3, 4, 5, 6, 7, 8, 9])]));
        yield return ("deep", M(("a", M(("b", M(("c", Byml.Array([M(("d", Byml.Array([Byml.Array([1, 2, 3, 4, 5, 6, 7, 8, 9, 10])]))), Byml.Array([bm])]))))))));
        yield return ("aligned-shared", M(("x", Byml.From(new byte[] { 9 }, 8)), ("y", "z")));
        Byml blob = Byml.From(new byte[] { 1, 2, 3, 4 }, 0x100);
        yield return ("aligned-anchored", Byml.Array([blob, blob, Byml.From(new byte[] { 7 })]));
        Byml sharedBin = Byml.From(new byte[] { 1, 2 });
        yield return ("binary-anchored", M(("a", sharedBin), ("b", sharedBin), ("c", Byml.Array([sharedBin]))));
        Byml dbl = Byml.From(1.5);
        yield return ("flow-anchored-scalars", M(("v", Byml.Array([big, dbl])), ("w", Byml.Array([big, dbl])), ("x", M(("a", big), ("b", dbl)))));
    }
}
