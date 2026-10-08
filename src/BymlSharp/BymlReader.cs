using System.Buffers.Binary;
using System.Text;

namespace BymlSharp;

internal static class BymlReader
{
    private const int HeaderSize = 0x10;
    private const int ContainerHeaderSize = 4;

    public static BymlFile Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize) throw new InvalidDataException("Too short to be a BYML document.");

        bool big = data[0] == (byte)'B' && data[1] == (byte)'Y';
        if (!big && !(data[0] == (byte)'Y' && data[1] == (byte)'B'))
            throw new InvalidDataException("Not a BYML document: expected a YB or BY magic.");

        Cursor c = new(data, big);

        ushort version = c.U16(0x02);
        if (!BymlVersion.IsSupported(version))
            throw new InvalidDataException(
                $"Unsupported BYML version {version}; this reads " +
                $"{BymlVersion.Minimum} to {BymlVersion.Maximum}.");

        int keyTable = c.I32(0x04);
        int stringTable = c.I32(0x08);
        int root = c.I32(0x0C);

        Tables t = new(ReadStringTable(c, keyTable), ReadStringTable(c, stringTable));

        // A document whose root offset is zero holds nothing at all, which is legal.
        Byml node = root == 0 ? Byml.Null : ReadNode(c, t, root, TypeAt(c, root));

        if (node.IsContainer)
        {
            int tablesEnd = Math.Max(TableEnd(c, keyTable), TableEnd(c, stringTable));
            new Replay(big).Walk(data, node, (Math.Max(tablesEnd, HeaderSize) + 3) & ~3);
        }

        return new BymlFile(node, version, big);
    }

    private static int TableEnd(in Cursor c, int offset)
        => offset == 0 ? 0 : offset + c.I32(offset + ContainerHeaderSize + CountAt(c, offset) * 4);

    private sealed class Tables(string[] keys, string[] strings)
    {
        public string[] Keys { get; } = keys;
        public string[] Strings { get; } = strings;

        // Offsets referenced from several places are one node, shared.
        public Dictionary<(int Offset, BymlType Type), Byml> Nodes { get; } = [];

        public int FirstValue { get; set; } = int.MaxValue;

        public int LastNodeFirstValue { get; set; } = int.MaxValue;
    }

    // The value section holds every out-of-line value in the order the writer meets them: map entries in
    // insertion order, each container once. The order the offsets suggest is usually that order; where a
    // value is shared the pointers name its last copy instead, so walk the section the way the writer
    // fills it and take, at each step, the first entry whose values are the bytes found there.
    private sealed class Replay(bool big)
    {
        private readonly HashSet<Byml> _visited = new(ReferenceEqualityComparer.Instance);
        private readonly List<Byml> _visitOrder = [];
        private int _position;

        public bool Walk(ReadOnlySpan<byte> data, Byml root, int start)
        {
            _position = start;
            return Container(data, root);
        }

        private bool Container(ReadOnlySpan<byte> data, Byml node)
        {
            if (!_visited.Add(node)) return true;
            _visitOrder.Add(node);

            if (node.IsMap) return Map(data, node);
            foreach (Byml child in BymlWriter.PlacementOrder(node))
                if (!Child(data, child)) return false;
            return true;
        }

        private bool Child(ReadOnlySpan<byte> data, Byml child)
            => child.IsContainer ? Container(data, child) : BymlWriter.IsInline(child.Type) || Value(data, child);

        private bool Map(ReadOnlySpan<byte> data, Byml node)
        {
            var entries = node.AsMap.ToList();
            int[] slots = [.. Enumerable.Range(0, entries.Count).Where(i => !BymlWriter.IsInline(entries[i].Value.Type))];
            List<KeyValuePair<string, Byml>> remaining = [.. slots.Select(i => entries[i])];
            List<KeyValuePair<string, Byml>> chosen = new(slots.Length);

            while (remaining.Count > 0)
            {
                int pick = -1;
                for (int i = 0; i < remaining.Count; i++)
                {
                    (int position, int visits) = (_position, _visitOrder.Count);
                    if (Child(data, remaining[i].Value))
                    {
                        pick = i;
                        break;
                    }

                    _position = position;
                    for (int v = _visitOrder.Count - 1; v >= visits; v--) _visited.Remove(_visitOrder[v]);
                    _visitOrder.RemoveRange(visits, _visitOrder.Count - visits);
                }

                if (pick < 0) return false;
                chosen.Add(remaining[pick]);
                remaining.RemoveAt(pick);
            }

            if (slots.Select((slot, i) => entries[slot].Key == chosen[i].Key).All(same => same)) return true;

            for (int i = 0; i < slots.Length; i++) entries[slots[i]] = chosen[i];
            IDictionary<string, Byml> map = node.AsMap;
            map.Clear();
            foreach (var (key, value) in entries) map.Add(key, value);
            return true;
        }

        private bool Value(ReadOnlySpan<byte> data, Byml node)
        {
            int at = _position;
            switch (node.Type)
            {
                case BymlType.Int64 or BymlType.UInt64 or BymlType.Double:
                    at = (at + 3) & ~3;
                    if (at + 8 > data.Length) return false;
                    long bits = big ? BinaryPrimitives.ReadInt64BigEndian(data[at..]) : BinaryPrimitives.ReadInt64LittleEndian(data[at..]);
                    long expected = node.Type switch
                    {
                        BymlType.Int64 => node.Int64,
                        BymlType.UInt64 => unchecked((long)node.UInt64),
                        _ => BitConverter.DoubleToInt64Bits(node.Double),
                    };
                    if (bits != expected) return false;
                    _position = at + 8;
                    return true;

                case BymlType.Binary or BymlType.BinaryAligned:
                    int header = 4;
                    if (node.Type is BymlType.BinaryAligned)
                    {
                        header = 8;
                        int alignment = node.BinaryAlignment;
                        if (alignment > 1) at = (at + header + alignment - 1) / alignment * alignment - header;
                    }

                    byte[] payload = node.Binary;
                    if (at + header + payload.Length > data.Length) return false;
                    int size = big ? BinaryPrimitives.ReadInt32BigEndian(data[at..]) : BinaryPrimitives.ReadInt32LittleEndian(data[at..]);
                    if (size != payload.Length || !data.Slice(at + header, size).SequenceEqual(payload)) return false;
                    _position = at + header + size;
                    return true;

                default:
                    return false;
            }
        }
    }

    private static BymlType TypeAt(in Cursor c, int offset) => (BymlType)c.U8(offset);

    private static int CountAt(in Cursor c, int offset) => c.U24(offset + 1);

    private static string[] ReadStringTable(in Cursor c, int offset)
    {
        if (offset == 0) return [];

        int count = CountAt(c, offset);
        if (count == 0) return [];

        string[] table = new string[count];
        for (int i = 0; i < count; i++)
        {
            int start = offset + c.I32(offset + ContainerHeaderSize + i * 4);
            int end = offset + c.I32(offset + ContainerHeaderSize + (i + 1) * 4);

            // The stored strings are null terminated as well as delimited.
            ReadOnlySpan<byte> span = c.Slice(start, Math.Max(end - start, 0));
            int nul = span.IndexOf((byte)0);
            table[i] = Encoding.UTF8.GetString(nul >= 0 ? span[..nul] : span);
        }
        return table;
    }

    private static Byml ReadNode(in Cursor c, Tables t, int value, BymlType type)
    {
        if (type is BymlType.Map or BymlType.Array or BymlType.HashMap32 or BymlType.HashMap64)
        {
            if (t.Nodes.TryGetValue((value, type), out Byml? shared))
            {
                t.LastNodeFirstValue = int.MaxValue;
                return shared;
            }

            int outer = t.FirstValue;
            t.FirstValue = int.MaxValue;
            Byml node = ReadValue(c, t, value, type);
            t.Nodes[(value, type)] = node;
            t.LastNodeFirstValue = t.FirstValue;
            t.FirstValue = Math.Min(outer, t.FirstValue);
            return node;
        }

        if (BymlWriter.IsInline(type))
        {
            t.LastNodeFirstValue = int.MaxValue;
            return ReadValue(c, t, value, type);
        }

        t.LastNodeFirstValue = value;
        t.FirstValue = Math.Min(t.FirstValue, value);
        if (!t.Nodes.TryGetValue((value, type), out Byml? same))
            t.Nodes[(value, type)] = same = ReadValue(c, t, value, type);
        return same;
    }

    private static Byml ReadValue(in Cursor c, Tables t, int value, BymlType type)
        => type switch
        {
            BymlType.Map => ReadMap(c, t, value),
            BymlType.Array => ReadArray(c, t, value),
            BymlType.HashMap32 => ReadHashMap(c, t, value, wide: false),
            BymlType.HashMap64 => ReadHashMap(c, t, value, wide: true),

            BymlType.String => Byml.Of(type, value >= 0 && value < t.Strings.Length ? t.Strings[value] : string.Empty),
            BymlType.Binary or BymlType.BinaryAligned => ReadBinary(c, value, type),

            BymlType.Bool => Byml.Of(type, value != 0),
            BymlType.Int => Byml.Of(type, value),
            BymlType.UInt => Byml.Of(type, unchecked((uint)value)),
            BymlType.Float => Byml.Of(type, BitConverter.Int32BitsToSingle(value)),

            BymlType.Int64 => Byml.Of(type, c.I64(value)),
            BymlType.UInt64 => Byml.Of(type, unchecked((ulong)c.I64(value))),
            BymlType.Double => Byml.Of(type, BitConverter.Int64BitsToDouble(c.I64(value))),

            BymlType.Null or BymlType.None => Byml.Null,
            _ => throw new InvalidDataException($"Unsupported BYML node type 0x{(byte)type:X2}."),
        };

    private static Byml ReadBinary(in Cursor c, int offset, BymlType type)
    {
        int size = c.I32(offset);

        if (type is BymlType.BinaryAligned)
            return Byml.Of(type, (c.Slice(offset + 8, size).ToArray(), c.I32(offset + 4)));

        return Byml.Of(BymlType.Binary, c.Slice(offset + 4, size).ToArray());
    }

    private static Byml ReadMap(in Cursor c, Tables t, int offset)
    {
        int count = CountAt(c, offset);
        var entries = new (string Key, BymlType Type, int Value)[count];

        for (int i = 0; i < count; i++)
        {
            int entry = offset + ContainerHeaderSize + i * 8;

            int keyIndex = c.U24(entry);
            string key = keyIndex >= 0 && keyIndex < t.Keys.Length ? t.Keys[keyIndex] : $"<{keyIndex}>";
            entries[i] = (key, (BymlType)c.U8(entry + 3), c.I32(entry + 4));
        }

        var nodes = new Byml[count];
        var firstValues = new int[count];
        for (int i = 0; i < count; i++)
        {
            nodes[i] = ReadNode(c, t, entries[i].Value, entries[i].Type);
            firstValues[i] = t.LastNodeFirstValue;
        }

        bool IsContainer(int i) => entries[i].Type is BymlType.Map or BymlType.Array
            or BymlType.HashMap32 or BymlType.HashMap64;

        int[] outOfLine = [.. Enumerable.Range(0, count).Where(i => !BymlWriter.IsInline(entries[i].Type))];
        int[] containers = [.. outOfLine.Where(IsContainer).OrderBy(i => entries[i].Value)];
        int[] values = [.. outOfLine.Where(i => !IsContainer(i)).OrderBy(i => entries[i].Value)];

        List<int> merged = new(outOfLine.Length);
        int ci = 0, vi = 0;
        while (ci < containers.Length || vi < values.Length)
        {
            bool containerFirst = vi == values.Length || (ci < containers.Length && (
                firstValues[containers[ci]] != int.MaxValue
                    ? firstValues[containers[ci]] < entries[values[vi]].Value
                    : string.CompareOrdinal(entries[containers[ci]].Key, entries[values[vi]].Key) < 0));
            merged.Add(containerFirst ? containers[ci++] : values[vi++]);
        }

        int[] order = [.. Enumerable.Range(0, count)];
        for (int i = 0; i < outOfLine.Length; i++) order[outOfLine[i]] = merged[i];

        Dictionary<string, Byml> map = new(count, StringComparer.Ordinal);
        foreach (int i in order) map[entries[i].Key] = nodes[i];

        return Byml.Of(BymlType.Map, map);
    }

    private static Byml ReadArray(in Cursor c, Tables t, int offset)
    {
        int count = CountAt(c, offset);
        List<Byml> array = new(count);

        int types = offset + ContainerHeaderSize;
        int values = types + ((count + 3) & ~3);

        for (int i = 0; i < count; i++)
            array.Add(ReadNode(c, t, c.I32(values + i * 4), (BymlType)c.U8(types + i)));

        return Byml.Of(BymlType.Array, array);
    }

    private static Byml ReadHashMap(in Cursor c, Tables t, int offset, bool wide)
    {
        int count = CountAt(c, offset);
        int entrySize = wide ? 0xC : 0x8;

        Dictionary<ulong, Byml> map = new(count);

        int entries = offset + ContainerHeaderSize;
        int types = entries + entrySize * count;

        for (int i = 0; i < count; i++)
        {
            int entry = entries + i * entrySize;
            ulong hash = wide ? unchecked((ulong)c.I64(entry)) : c.U32(entry);
            int value = c.I32(entry + (wide ? 8 : 4));

            map[hash] = ReadNode(c, t, value, (BymlType)c.U8(types + i));
        }

        return Byml.Of(wide ? BymlType.HashMap64 : BymlType.HashMap32, map);
    }

    private readonly ref struct Cursor(ReadOnlySpan<byte> data, bool big)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private readonly bool _big = big;

        public byte U8(int at) => At(at, 1)[0];

        public ushort U16(int at) => _big
            ? BinaryPrimitives.ReadUInt16BigEndian(At(at, 2))
            : BinaryPrimitives.ReadUInt16LittleEndian(At(at, 2));

        public int I32(int at) => _big
            ? BinaryPrimitives.ReadInt32BigEndian(At(at, 4))
            : BinaryPrimitives.ReadInt32LittleEndian(At(at, 4));

        public uint U32(int at) => unchecked((uint)I32(at));

        public int U24(int at)
        {
            ReadOnlySpan<byte> b = At(at, 3);
            return _big
                ? (b[0] << 16) | (b[1] << 8) | b[2]
                : b[0] | (b[1] << 8) | (b[2] << 16);
        }

        public long I64(int at) => _big
            ? BinaryPrimitives.ReadInt64BigEndian(At(at, 8))
            : BinaryPrimitives.ReadInt64LittleEndian(At(at, 8));

        public ReadOnlySpan<byte> Slice(int at, int length) => At(at, length);

        private ReadOnlySpan<byte> At(int at, int length)
        {
            if (at < 0 || length < 0 || at + length > _data.Length)
                throw new InvalidDataException(
                    $"BYML node runs past the end of the document: {length} bytes at 0x{at:X}.");

            return _data.Slice(at, length);
        }
    }
}
