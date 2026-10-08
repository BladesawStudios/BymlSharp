using SarcSharp;
using Yaz0Sharp;
using ZsDicSharp;

namespace BymlSharp.Tests;

// Corpus tests run only when BOTW_ROMFS / TOTK_ROMFS point at a romfs root; otherwise they are skipped.
internal static class Corpus
{
    internal const string BotwEnv = "BOTW_ROMFS";
    internal const string TotkEnv = "TOTK_ROMFS";

    internal static string? BotwRoot => Root(BotwEnv);
    internal static string? TotkRoot => Root(TotkEnv);

    private static string? Root(string env)
        => Environment.GetEnvironmentVariable(env) is { } root && Directory.Exists(root) ? root : null;

    /// <summary>Visits every file under the root in parallel, decompressed and with SARC packs opened at any depth.</summary>
    internal static void ForEach(string root, Action<string, byte[]> visit)
    {
        using ZsDic? zs = File.Exists(Path.Combine(root, "Pack", "ZsDic.pack.zs")) ? ZsDic.FromRomfs(root) : null;
        Parallel.ForEach(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories), path =>
        {
            byte[] data = File.ReadAllBytes(path);
            if (zs is not null && ZsDic.IsCompressed(data)) data = zs.Decompress(data, path);
            Open(Path.GetRelativePath(root, path), data, 0, visit);
        });
    }

    private static void Open(string name, byte[] data, int depth, Action<string, byte[]> visit)
    {
        data = Yaz0.DecompressIfNeeded(data);
        if (data.AsSpan().StartsWith("SARC"u8) && depth < 4)
        {
            foreach (SarcEntry entry in SarcFile.FromBinary(data).Entries) Open($"{name}/{entry.Name}", entry.Data, depth + 1, visit);
            return;
        }
        visit(name, data);
    }
}

public sealed class BotwCorpusFactAttribute : FactAttribute
{
    public BotwCorpusFactAttribute()
    {
        if (Corpus.BotwRoot is null) Skip = $"Set {Corpus.BotwEnv} to a BotW romfs root to run the corpus tests.";
    }
}

public sealed class TotkCorpusFactAttribute : FactAttribute
{
    public TotkCorpusFactAttribute()
    {
        if (Corpus.TotkRoot is null) Skip = $"Set {Corpus.TotkEnv} to a TotK romfs root to run the corpus tests.";
    }
}
