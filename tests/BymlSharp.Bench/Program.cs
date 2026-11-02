using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using BymlSharp;
using BymlSharp.Tests;

// usage: BymlSharp.Bench <romfs> [filter ...] [--limit N] [--iters N] [--yaml]
//   filter: substring(s) of the in-romfs path (e.g. bcett, .bgyml); none = every BYML
if (args[0] == "--synthetic") { SyntheticDocuments.Print(); return; }
if (args[0] == "--synthcheck") { YamlCheck.Run(SyntheticDocuments.Roots()); return; }
string root = args[0];
List<string> filters = [];
int limit = int.MaxValue, iters = 7;
bool yaml = false, yamlCheck = false;
string? only = null;
for (int i = 1; i < args.Length; i++)
{
    if (args[i] == "--limit") limit = int.Parse(args[++i]);
    else if (args[i] == "--iters") iters = int.Parse(args[++i]);
    else if (args[i] == "--yaml") yaml = true;
    else if (args[i] == "--yamlcheck") yamlCheck = true;
    else if (args[i] == "--only") only = args[++i];
    else filters.Add(args[i]);
}

bool IsByml(ReadOnlySpan<byte> d)
{
    if (d.Length < 16) return false;
    if (d.StartsWith("BY"u8)) return BymlVersion.IsSupported(BinaryPrimitives.ReadUInt16BigEndian(d[2..]));
    if (d.StartsWith("YB"u8)) return BymlVersion.IsSupported(BinaryPrimitives.ReadUInt16LittleEndian(d[2..]));
    return false;
}

// The decompressed selection is cached next to the build, so reruns skip the (slow) romfs walk.
string cache = Path.Combine(Path.GetTempPath(), "byml-bench-" + string.Join("_", filters.Append(limit.ToString())).Replace('.', '-') + ".bin");
(string Name, byte[] Data)[] docs;
Stopwatch load = Stopwatch.StartNew();
if (File.Exists(cache))
{
    using var r = new BinaryReader(File.OpenRead(cache));
    docs = new (string, byte[])[r.ReadInt32()];
    for (int i = 0; i < docs.Length; i++) docs[i] = (r.ReadString(), r.ReadBytes(r.ReadInt32()));
}
else
{
    Console.WriteLine($"Loading corpus from {root} ...");
    ConcurrentBag<(string Name, byte[] Data)> found = [];
    Corpus.ForEach(root, (name, data) =>
    {
        if (!IsByml(data)) return;
        if (filters.Count > 0 && !filters.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase))) return;
        found.Add((name, data));
    });
    docs = found.OrderBy(d => d.Name, StringComparer.Ordinal).Take(limit).ToArray();
    using var w = new BinaryWriter(File.Create(cache));
    w.Write(docs.Length);
    foreach (var d in docs) { w.Write(d.Name); w.Write(d.Data.Length); w.Write(d.Data); }
}
long totalBytes = docs.Sum(d => (long)d.Data.Length);
Console.WriteLine($"{docs.Length} documents, {totalBytes / 1048576.0:F1} MiB, loaded in {load.Elapsed.TotalSeconds:F1}s");
if (docs.Length == 0) return;

if (yamlCheck)
{
    YamlCheck.Run(docs.Select(d => (d.Name, BymlFile.FromBinary(d.Data).Root)));
    return;
}

BymlFile[] parsed = new BymlFile[docs.Length];
bool[] exact = new bool[docs.Length];
int mismatches = 0;
for (int i = 0; i < docs.Length; i++)
{
    parsed[i] = BymlFile.FromBinary(docs[i].Data);
    exact[i] = parsed[i].Write().AsSpan().SequenceEqual(docs[i].Data);
    if (!exact[i]) mismatches++;
}
Console.WriteLine($"byte-exact rewrites: {docs.Length - mismatches}/{docs.Length}");

void Measure(string label, Action run)
{
    run(); run(); // warm up / tier up
    var times = new double[iters];
    long alloc = 0;
    int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
    TimeSpan pause0 = GC.GetTotalPauseDuration();
    for (int i = 0; i < iters; i++)
    {
        GC.Collect(); GC.WaitForPendingFinalizers();
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        long t0 = Stopwatch.GetTimestamp();
        run();
        times[i] = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        alloc = GC.GetAllocatedBytesForCurrentThread() - a0;
    }
    Console.WriteLine($"  GCs gen0 {GC.CollectionCount(0) - g0} gen1 {GC.CollectionCount(1) - g1} gen2 {GC.CollectionCount(2) - g2}, pause {(GC.GetTotalPauseDuration() - pause0).TotalMilliseconds / iters:F0} ms/iter");
    Array.Sort(times);
    double best = times[0], med = times[iters / 2];
    Console.WriteLine($"{label,-10} best {best,9:F1} ms  median {med,9:F1} ms  {totalBytes / 1048576.0 / (best / 1000):F0} MiB/s  alloc {alloc / 1048576.0,8:F1} MiB");
}

if (only is "read" or "write" or "toyaml" or "fromyaml") iters = Math.Max(iters, 30);
if (only is null or "read") Measure("read", () => { foreach (var d in docs) BymlFile.FromBinary(d.Data); });
if (only is null or "write") Measure("write", () => { foreach (var f in parsed) f.Write(); });
if (yaml || only is "toyaml" or "fromyaml")
{
    if (only is null or "toyaml") Measure("toYaml", () => { foreach (var f in parsed) f.Root.ToYaml(); });
    string[] texts = parsed.Select(f => f.Root.ToYaml()).ToArray();
    if (only is null or "fromyaml") Measure("fromYaml", () => { foreach (var t in texts) Byml.FromYaml(t); });
}

if (only is not null) return;
// slowest individual reads, to see where per-file hot spots live
var per = new List<(string Name, int Size, double Ms)>();
for (int i = 0; i < docs.Length; i++)
{
    long t0 = Stopwatch.GetTimestamp();
    BymlFile.FromBinary(docs[i].Data);
    per.Add((docs[i].Name, docs[i].Data.Length, Stopwatch.GetElapsedTime(t0).TotalMilliseconds));
}
Console.WriteLine("slowest reads:");
foreach (var p in per.OrderByDescending(p => p.Ms).Take(5))
    Console.WriteLine($"  {p.Ms,8:F2} ms  {p.Size / 1024,7} KiB  {p.Name}");
