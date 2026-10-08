using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace BymlSharp.Tests;

public class CorpusTests
{
    // PtclBin ignores its own 4 KiB alignment and its size runs into the Textures array after it; patched in
    // place, so no layout reproduces it.
    private static readonly HashSet<string> NotByteExact =
    [
        Path.Combine("Effect", "Animal_Crow_A.Nin_NX_NVN.esetb.byml.zs"),
    ];

    [BotwCorpusFact]
    public void BotwRewritesByteForByteDirectlyAndThroughYaml() => Check(Corpus.BotwRoot!, 1800);

    [TotkCorpusFact]
    public void TotkRewritesByteForByteDirectlyAndThroughYaml() => Check(Corpus.TotkRoot!, 470000);

    private static void Check(string root, int expected)
    {
        ConcurrentBag<string> failures = [];
        int count = 0;

        Corpus.ForEach(root, (name, data) =>
        {
            if (!IsByml(data)) return;
            Interlocked.Increment(ref count);
            try
            {
                BymlFile file = BymlFile.FromBinary(data);
                bool exact = !NotByteExact.Contains(name);
                if (exact && !file.Write().AsSpan().SequenceEqual(data))
                {
                    failures.Add($"{name}: rewrite differs");
                    return;
                }

                string yaml = file.Root.ToYaml();
                Byml back = Byml.FromYaml(yaml);
                byte[] rebuilt = new BymlFile(back, file.Version, file.BigEndian).Write();
                if (back.ToYaml() != yaml) failures.Add($"{name}: YAML re-emit differs");
                else if (exact && !rebuilt.AsSpan().SequenceEqual(data)) failures.Add($"{name}: YAML rebuild differs");
                else if (!exact && Byml.FromBinary(rebuilt).ToYaml() != yaml) failures.Add($"{name}: YAML rebuild changes the tree");
            }
            catch (Exception e)
            {
                failures.Add($"{name}: {e.GetType().Name}: {e.Message}");
            }
        });

        Assert.True(count >= expected, $"Expected the full corpus, found {count} BYML documents.");
        Assert.True(failures.IsEmpty, $"{failures.Count} failures:{Environment.NewLine}{string.Join(Environment.NewLine, failures.Order().Take(20))}");
    }

    private static bool IsByml(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) return false;
        if (data.StartsWith("BY"u8)) return BymlVersion.IsSupported(BinaryPrimitives.ReadUInt16BigEndian(data[2..]));
        if (data.StartsWith("YB"u8)) return BymlVersion.IsSupported(BinaryPrimitives.ReadUInt16LittleEndian(data[2..]));
        return false;
    }
}
