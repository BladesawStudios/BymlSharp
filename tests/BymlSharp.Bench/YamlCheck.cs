using BymlSharp;

// Differential check of the hand-written YAML writer and reader against the YamlDotNet-based ones.
internal static class YamlCheck
{
    public static void Run(IEnumerable<(string Name, Byml Root)> docs)
    {
        int total = 0, writeFallback = 0, writeDiff = 0, readFallback = 0, readDiff = 0, legacyRejects = 0;
        List<string> problems = [];

        foreach ((string name, Byml root) in docs)
        {
            total++;
            foreach (bool anchors in new[] { false, true })
            {
                string legacy = BymlYamlLegacy.Write(root, anchors);
                string? fast = BymlYamlWriter.TryWrite(root, anchors);
                if (fast is null) { writeFallback++; if (problems.Count < 40) problems.Add($"WRITE-FALLBACK {name} anchors={anchors}"); }
                else if (fast != legacy)
                {
                    writeDiff++;
                    if (problems.Count < 40) problems.Add($"WRITE {name} anchors={anchors}: {FirstDifference(fast, legacy)}");
                }

                Byml? viaLegacy = null;
                try { viaLegacy = BymlYamlLegacy.Read(legacy); } catch (Exception) { legacyRejects++; }

                try
                {
                    Byml viaFast = BymlYamlReader.Read(legacy);
                    if (viaLegacy is null)
                    {
                        if (BymlYamlLegacy.Write(viaFast, anchors) != legacy)
                        {
                            readDiff++;
                            if (problems.Count < 40) problems.Add($"READ {name} anchors={anchors}: reference rejects it and the fast reader's tree doesn't write back the text");
                        }
                    }
                    else if (BymlYamlLegacy.Write(viaFast, true) != BymlYamlLegacy.Write(viaLegacy, true) || !Bin(viaFast).AsSpan().SequenceEqual(Bin(viaLegacy)))
                    {
                        readDiff++;
                        if (problems.Count < 40) problems.Add($"READ {name} anchors={anchors}");
                    }
                }
                catch (BymlYamlReader.Unsupported)
                {
                    readFallback++;
                    if (problems.Count < 40) problems.Add($"READ-FALLBACK {name} anchors={anchors}");
                }
            }
        }

        Console.WriteLine($"{total} docs: write fallback {writeFallback}, write diff {writeDiff}, read fallback {readFallback}, read diff {readDiff} (reference rejects its own text: {legacyRejects})");
        foreach (string p in problems) Console.WriteLine("  " + p);
    }

    private static byte[] Bin(Byml root) => root.IsContainer || root.IsNull ? root.ToBinary() : [];

    private static string FirstDifference(string a, string b)
    {
        int i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        string Around(string s) => s.Substring(Math.Max(0, i - 40), Math.Min(100, s.Length - Math.Max(0, i - 40))).Replace("\r", "\r").Replace("\n", "\n");
        return $"@{i}: fast «{Around(a)}» vs legacy «{Around(b)}»";
    }
}
