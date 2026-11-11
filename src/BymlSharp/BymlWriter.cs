using System.Text;

namespace BymlSharp;

internal static partial class BymlWriter
{
    private const int HeaderSize = 0x10;
    private const int ContainerHeaderSize = 4;

    public static byte[] Write(Byml root, int version, bool bigEndian, bool shareIdenticalContainers = false)
    {
        HashSet<string> keySet = new(StringComparer.Ordinal);
        HashSet<string> stringSet = new(StringComparer.Ordinal);
        Census census = new();
        Collect(root, keySet, stringSet, census);

        string[] keys = Sorted(keySet);
        string[] strings = Sorted(stringSet);

        Dictionary<string, int> keyIndex = Index(keys);
        Dictionary<string, int> stringIndex = Index(strings);

        using Buffer buffer = new(bigEndian);
        buffer.Reserve(HeaderSize);

        int keyTable = WriteStringTable(buffer, keys);
        int stringTable = WriteStringTable(buffer, strings);

        IEqualityComparer<Byml> sharing = shareIdenticalContainers ? ContentComparer.Instance : ReferenceEqualityComparer.Instance;

        int rootOffset = 0;
        if (root.IsContainer)
        {
            buffer.Align(4);
            Dictionary<Byml, int> values = new(census.Values, ReferenceEqualityComparer.Instance);
            WriteValues(buffer, root, values, new(census.Containers, sharing));

            buffer.Align(4);
            rootOffset = buffer.Length;
            WriteContainer(buffer, root, new Placement(keyIndex, stringIndex, strings, values, new(census.Containers, sharing), [], new MapLayout()));
        }
        else if (!root.IsNull)
        {
            throw new InvalidOperationException(
                $"A BYML document's root must be a container or null, not {root.Type}.");
        }

        buffer.SetU8(0, (byte)(bigEndian ? 'B' : 'Y'));
        buffer.SetU8(1, (byte)(bigEndian ? 'Y' : 'B'));
        buffer.SetU16(0x02, (ushort)version);
        buffer.SetI32(0x04, keyTable);
        buffer.SetI32(0x08, stringTable);
        buffer.SetI32(0x0C, rootOffset);

        return buffer.ToArray();
    }

    private static Dictionary<string, int> Index(string[] values)
    {
        Dictionary<string, int> map = new(values.Length, StringComparer.Ordinal);
        for (int i = 0; i < values.Length; i++) map[values[i]] = i;
        return map;
    }

    private static string[] Sorted(HashSet<string> set)
    {
        string[] sorted = new string[set.Count];
        set.CopyTo(sorted);
        Array.Sort(sorted, StringComparer.Ordinal);
        return sorted;
    }

    private sealed class Census
    {
        public int Containers;
        public int Values;
    }

    private static void Collect(Byml node, HashSet<string> keys, HashSet<string> strings, Census census)
    {
        if (node.IsContainer) census.Containers++;
        else if (!IsInline(node.Type)) census.Values++;

        switch (node.Type)
        {
            case BymlType.Map:
                foreach ((string key, Byml child) in node.AsMap)
                {
                    keys.Add(key);
                    Collect(child, keys, strings, census);
                }
                break;

            case BymlType.Array:
                List<Byml> items = node.AsArray;
                for (int i = 0; i < items.Count; i++) Collect(items[i], keys, strings, census);
                break;

            case BymlType.HashMap32 or BymlType.HashMap64:
                foreach (Byml child in node.AsHashMap.Values) Collect(child, keys, strings, census);
                break;

            case BymlType.String:
                strings.Add(node.String);
                break;
        }
    }

    private static int WriteStringTable(Buffer buffer, string[] values)
    {
        if (values.Length == 0) return 0;

        buffer.Align(4);
        int offset = buffer.Length;

        AddHeader(buffer, BymlType.StringTable, values.Length);

        int running = ContainerHeaderSize + (values.Length + 1) * 4;
        buffer.AddI32(running);
        foreach (string value in values)
            buffer.AddI32(running += Encoding.UTF8.GetByteCount(value) + 1);

        foreach (string value in values)
        {
            buffer.AddUtf8(value);
            buffer.AddU8(0);
        }

        buffer.Align(4);
        return offset;
    }

    private static void AddHeader(Buffer buffer, BymlType type, int count)
    {
        buffer.AddU8((byte)type);
        buffer.AddU24(count);
    }

    internal static IEnumerable<Byml> PlacementOrder(Byml node) => node.Type switch
    {
        BymlType.Map => node.AsMap.Values,
        BymlType.Array => node.AsArray,
        _ => node.AsHashMap.OrderBy(e => e.Key).Select(e => e.Value),
    };

    private static void WriteValues(Buffer buffer, Byml node, Dictionary<Byml, int> offsets, HashSet<Byml> visited)
    {
        if (!visited.Add(node)) return;

        switch (node.Type)
        {
            case BymlType.Map:
                foreach (Byml child in node.AsMap.Values) WriteValue(buffer, child, offsets, visited);
                break;

            case BymlType.Array:
                List<Byml> items = node.AsArray;
                for (int i = 0; i < items.Count; i++) WriteValue(buffer, items[i], offsets, visited);
                break;

            default:
                foreach (Byml child in PlacementOrder(node)) WriteValue(buffer, child, offsets, visited);
                break;
        }
    }

    private static void WriteValue(Buffer buffer, Byml child, Dictionary<Byml, int> offsets, HashSet<Byml> visited)
    {
        if (child.IsContainer)
        {
            WriteValues(buffer, child, offsets, visited);
        }
        else if (!IsInline(child.Type))
        {
            if (child.Type is BymlType.BinaryAligned) buffer.AlignPayload(8, child.BinaryAlignment);
            else if (child.Type is not BymlType.Binary) buffer.Align(4);
            offsets[child] = buffer.Length;
            WriteOutOfLine(buffer, child);
        }
    }

    internal static bool IsInline(BymlType type) => type is BymlType.String or BymlType.Bool or BymlType.Int
        or BymlType.UInt or BymlType.Float or BymlType.Null or BymlType.None;

    private static int StringIndex(Byml node, string[] table, Dictionary<string, int> strings)
    {
        string value = node.String;
        int hint = node.StringIndexHint;
        return (uint)hint < (uint)table.Length && ReferenceEquals(table[hint], value) ? hint : strings[value];
    }

    private static bool Inline(Buffer buffer, Byml node, string[] table, Dictionary<string, int> strings, int patchAt)
    {
        switch (node.Type)
        {
            case BymlType.String: buffer.SetI32(patchAt, StringIndex(node, table, strings)); return true;
            case BymlType.Bool: buffer.SetI32(patchAt, node.Bool ? 1 : 0); return true;
            case BymlType.Int: buffer.SetI32(patchAt, node.Int); return true;
            case BymlType.UInt: buffer.SetI32(patchAt, unchecked((int)node.UInt)); return true;
            case BymlType.Float: buffer.SetI32(patchAt, BitConverter.SingleToInt32Bits(node.Float)); return true;
            case BymlType.Null or BymlType.None: buffer.SetI32(patchAt, 0); return true;
            default: return false;
        }
    }

    private static void WriteOutOfLine(Buffer buffer, Byml node)
    {
        switch (node.Type)
        {
            case BymlType.Int64: buffer.AddI64(node.Int64); break;
            case BymlType.UInt64: buffer.AddI64(unchecked((long)node.UInt64)); break;
            case BymlType.Double: buffer.AddI64(BitConverter.DoubleToInt64Bits(node.Double)); break;

            case BymlType.Binary:
                buffer.AddI32(node.Binary.Length);
                buffer.AddBytes(node.Binary);
                break;

            case BymlType.BinaryAligned:
                buffer.AddI32(node.Binary.Length);
                buffer.AddI32(node.BinaryAlignment);
                buffer.AddBytes(node.Binary);
                break;

            default:
                throw new InvalidOperationException($"{node.Type} is not written out of line.");
        }
    }

}
