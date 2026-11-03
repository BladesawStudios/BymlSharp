namespace BymlSharp;

internal static partial class BymlWriter
{
    internal sealed class ContentComparer : IEqualityComparer<Byml>
    {
        public static readonly ContentComparer Instance = new();

        public bool Equals(Byml? a, Byml? b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a is null || b is null || a.Type != b.Type || a.Count != b.Count) return false;

            return a.Type switch
            {
                BymlType.Map => a.AsMap.All(e => b.AsMap.TryGetValue(e.Key, out Byml? other) && Equals(e.Value, other)),
                BymlType.Array => a.AsArray.Zip(b.AsArray).All(p => Equals(p.First, p.Second)),
                BymlType.HashMap32 or BymlType.HashMap64
                    => a.AsHashMap.All(e => b.AsHashMap.TryGetValue(e.Key, out Byml? other) && Equals(e.Value, other)),
                BymlType.Binary or BymlType.BinaryAligned
                    => a.BinaryAlignment == b.BinaryAlignment && a.Binary.AsSpan().SequenceEqual(b.Binary),
                _ => Equals(a.Value, b.Value),
            };
        }

        public int GetHashCode(Byml node)
        {
            HashCode hash = new();
            hash.Add(node.Type);
            hash.Add(node.Count);
            switch (node.Type)
            {
                case BymlType.Map:
                    foreach (var (key, child) in node.AsMap.OrderBy(e => e.Key, StringComparer.Ordinal))
                    {
                        hash.Add(key, StringComparer.Ordinal);
                        hash.Add(GetHashCode(child));
                    }
                    break;
                case BymlType.Array:
                    foreach (Byml child in node.AsArray) hash.Add(GetHashCode(child));
                    break;
                case BymlType.HashMap32 or BymlType.HashMap64:
                    foreach (var (key, child) in node.AsHashMap.OrderBy(e => e.Key))
                    {
                        hash.Add(key);
                        hash.Add(GetHashCode(child));
                    }
                    break;
                case BymlType.Binary or BymlType.BinaryAligned:
                    hash.AddBytes(node.Binary);
                    break;
                default:
                    hash.Add(node.Value);
                    break;
            }
            return hash.ToHashCode();
        }
    }
}
