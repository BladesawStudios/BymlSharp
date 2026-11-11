namespace BymlSharp;

internal static partial class BymlReader
{
    private const int StackLimit = 128;

    private struct MapEntry
    {
        public string Key;
        public Byml Node;
        public int Value;
        public int FirstValue;
        public BymlType Type;
    }

    private static bool IsContainerType(BymlType type)
        => type is BymlType.Map or BymlType.Array or BymlType.HashMap32 or BymlType.HashMap64;

    private static Byml ReadMap(in Cursor c, Tables t, int offset)
    {
        int count = CountAt(c, offset);
        Dictionary<string, Byml> map = new(count, StringComparer.Ordinal);
        if (count == 0) return Byml.Of(BymlType.Map, map);

        MapEntry[] entries = new MapEntry[count];
        Span<long> sortKeys = count <= StackLimit ? stackalloc long[count] : new long[count];
        Span<int> merged = count <= StackLimit ? stackalloc int[count] : new int[count];

        {
            string[] keys = t.Keys;
            for (int i = 0; i < count; i++)
            {
                int entry = offset + ContainerHeaderSize + i * 8;

                int keyIndex = c.U24(entry);
                ref MapEntry e = ref entries[i];
                e.Key = (uint)keyIndex < (uint)keys.Length ? keys[keyIndex] : $"<{keyIndex}>";
                e.Type = (BymlType)c.U8(entry + 3);
                e.Value = c.I32(entry + 4);
            }

            for (int i = 0; i < count; i++)
            {
                ref MapEntry e = ref entries[i];
                e.Node = ReadNode(c, t, e.Value, e.Type);
                e.FirstValue = t.LastNodeFirstValue;
            }

            int containers = 0, values = 0;
            for (int i = 0; i < count; i++)
            {
                if (BymlWriter.IsInline(entries[i].Type)) continue;
                if (IsContainerType(entries[i].Type)) containers++;
                else values++;
            }

            int ci = 0, vi = containers;
            for (int i = 0; i < count; i++)
            {
                ref MapEntry e = ref entries[i];
                if (BymlWriter.IsInline(e.Type)) continue;
                long key = ((long)e.Value << 32) | (uint)i;
                if (IsContainerType(e.Type)) sortKeys[ci++] = key;
                else sortKeys[vi++] = key;
            }

            Span<long> containerKeys = sortKeys[..containers];
            Span<long> valueKeys = sortKeys.Slice(containers, values);
            containerKeys.Sort();
            valueKeys.Sort();

            int m = 0;
            ci = 0;
            vi = 0;
            while (ci < containers || vi < values)
            {
                bool containerFirst;
                if (vi == values) containerFirst = true;
                else if (ci == containers) containerFirst = false;
                else
                {
                    ref MapEntry container = ref entries[(int)containerKeys[ci]];
                    ref MapEntry value = ref entries[(int)valueKeys[vi]];
                    containerFirst = container.FirstValue != int.MaxValue
                        ? container.FirstValue < value.Value
                        : string.CompareOrdinal(container.Key, value.Key) < 0;
                }
                merged[m++] = containerFirst ? (int)containerKeys[ci++] : (int)valueKeys[vi++];
            }

            m = 0;
            for (int i = 0; i < count; i++)
            {
                ref MapEntry e = ref entries[BymlWriter.IsInline(entries[i].Type) ? i : merged[m++]];
                map[e.Key] = e.Node;
            }
        }

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
}
