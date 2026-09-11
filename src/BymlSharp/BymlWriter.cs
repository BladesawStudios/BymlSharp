using System.Buffers.Binary;
using System.Text;

namespace BymlSharp;

/// <summary>
/// Writes a node tree back to the binary form.
/// </summary>
/// <remarks>
/// Two passes. The first collects every map key and every string value, because both live in
/// tables that the rest of the document indexes into and the game binary-searches, so they
/// have to be sorted before anything can reference them. The second writes the header, the
/// two tables, and then the nodes - each container writing its children's offsets as it goes
/// and appending the children after itself.
/// </remarks>
internal static class BymlWriter
{
    private const int HeaderSize = 0x10;
    private const int ContainerHeaderSize = 4;

    public static byte[] Write(Byml root, int version, bool bigEndian)
    {
        SortedSet<string> keySet = new(StringComparer.Ordinal);
        SortedSet<string> stringSet = new(StringComparer.Ordinal);
        Collect(root, keySet, stringSet);

        string[] keys = [.. keySet];
        string[] strings = [.. stringSet];

        Dictionary<string, int> keyIndex = Index(keys);
        Dictionary<string, int> stringIndex = Index(strings);

        Buffer buffer = new(bigEndian);
        buffer.Reserve(HeaderSize);

        int keyTable = WriteStringTable(buffer, keys);
        int stringTable = WriteStringTable(buffer, strings);

        int rootOffset = 0;
        if (root.IsContainer)
        {
            buffer.Align(4);
            rootOffset = buffer.Length;
            WriteContainer(buffer, root, keyIndex, stringIndex);
        }
        else if (!root.IsNull)
        {
            throw new InvalidOperationException(
                $"A BYML document's root must be a container or null, not {root.Type}.");
        }

        // "YB" little, "BY" big - the magic is the one field whose order is fixed.
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

    private static void Collect(Byml node, SortedSet<string> keys, SortedSet<string> strings)
    {
        switch (node.Type)
        {
            case BymlType.Map:
                foreach ((string key, Byml child) in node.AsMap)
                {
                    keys.Add(key);
                    Collect(child, keys, strings);
                }
                break;

            case BymlType.Array:
                foreach (Byml child in node.AsArray) Collect(child, keys, strings);
                break;

            case BymlType.HashMap32 or BymlType.HashMap64:
                foreach (Byml child in node.AsHashMap.Values) Collect(child, keys, strings);
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

        // One offset per string plus a final one marking where the last ends, all relative to
        // the container.
        int running = ContainerHeaderSize + (values.Length + 1) * 4;
        buffer.AddI32(running);
        foreach (string value in values)
            buffer.AddI32(running += Encoding.UTF8.GetByteCount(value) + 1);

        foreach (string value in values)
        {
            buffer.AddBytes(Encoding.UTF8.GetBytes(value));
            buffer.AddU8(0);
        }

        buffer.Align(4);
        return offset;
    }

    /// <summary>
    /// Writes a container header: the type byte, then a 24-bit count.
    /// </summary>
    /// <remarks>
    /// Only the count is byte-ordered. Packing both into one integer works little endian and
    /// puts the type at the wrong end big endian.
    /// </remarks>
    private static void AddHeader(Buffer buffer, BymlType type, int count)
    {
        buffer.AddU8((byte)type);
        buffer.AddU24(count);
    }

    /// <summary>
    /// Writes a container and everything under it.
    /// </summary>
    /// <remarks>
    /// A node that does not fit in four bytes - every container, and the 64-bit values - is
    /// referenced by offset instead. Those are staged while the container's own body is
    /// written, then appended one at a time with their offsets patched back in, which keeps a
    /// container contiguous and its children immediately after it.
    /// </remarks>
    private static void WriteContainer(
        Buffer buffer, Byml node, Dictionary<string, int> keys, Dictionary<string, int> strings)
    {
        List<(int Patch, Byml Node)> staged = [];

        void WriteChild(Byml child, int patchAt)
        {
            if (Inline(buffer, child, strings, patchAt)) return;
            staged.Add((patchAt, child));
        }

        switch (node.Type)
        {
            case BymlType.Map:
            {
                // Entries are sorted by key, as the game binary-searches them.
                var entries = node.AsMap.OrderBy(e => e.Key, StringComparer.Ordinal).ToList();
                AddHeader(buffer, BymlType.Map, entries.Count);

                foreach ((string key, Byml child) in entries)
                {
                    // Three bytes of key index, then the type - ordered like the header.
                    buffer.AddU24(keys[key]);
                    buffer.AddU8((byte)child.Type);
                    int patch = buffer.Length;
                    buffer.AddI32(0);
                    WriteChild(child, patch);
                }
                break;
            }

            case BymlType.Array:
            {
                IList<Byml> items = node.AsArray;
                AddHeader(buffer, BymlType.Array, items.Count);

                foreach (Byml child in items) buffer.AddU8((byte)child.Type);
                buffer.Align(4);

                foreach (Byml child in items)
                {
                    int patch = buffer.Length;
                    buffer.AddI32(0);
                    WriteChild(child, patch);
                }
                break;
            }

            case BymlType.HashMap32 or BymlType.HashMap64:
            {
                bool wide = node.Type is BymlType.HashMap64;

                // Sorted by hash, for the same reason maps are sorted by key.
                var entries = node.AsHashMap.OrderBy(e => e.Key).ToList();
                AddHeader(buffer, node.Type, entries.Count);

                foreach ((ulong hash, Byml child) in entries)
                {
                    if (wide) buffer.AddI64(unchecked((long)hash));
                    else buffer.AddI32(unchecked((int)(uint)hash));

                    int patch = buffer.Length;
                    buffer.AddI32(0);
                    WriteChild(child, patch);
                }

                foreach ((_, Byml child) in entries) buffer.AddU8((byte)child.Type);
                buffer.Align(4);
                break;
            }

            default:
                throw new InvalidOperationException($"{node.Type} is not a container.");
        }

        foreach ((int patch, Byml child) in staged)
        {
            if (child.IsContainer) buffer.Align(4);
            else buffer.Align(8);              // the 64-bit values want eight

            buffer.SetI32(patch, buffer.Length);

            if (child.IsContainer) WriteContainer(buffer, child, keys, strings);
            else WriteOutOfLine(buffer, child);
        }
    }

    /// <summary>Writes a value that fits in the node itself, and says whether it did.</summary>
    private static bool Inline(Buffer buffer, Byml node, Dictionary<string, int> strings, int patchAt)
    {
        switch (node.Type)
        {
            case BymlType.String: buffer.SetI32(patchAt, strings[node.String]); return true;
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

    /// <summary>A growable buffer that writes in the document's byte order.</summary>
    private sealed class Buffer(bool bigEndian)
    {
        private byte[] _data = new byte[4096];
        private readonly bool _big = bigEndian;

        public int Length { get; private set; }

        public void Reserve(int count)
        {
            Ensure(count);
            Length += count;
        }

        public void Align(int to)
        {
            int padded = (Length + to - 1) / to * to;
            Reserve(padded - Length);
        }

        public void AddU8(byte value)
        {
            Ensure(1);
            _data[Length++] = value;
        }

        public void AddBytes(ReadOnlySpan<byte> value)
        {
            Ensure(value.Length);
            value.CopyTo(_data.AsSpan(Length));
            Length += value.Length;
        }

        public void AddU24(int value)
        {
            if (_big) { AddU8((byte)(value >> 16)); AddU8((byte)(value >> 8)); AddU8((byte)value); }
            else { AddU8((byte)value); AddU8((byte)(value >> 8)); AddU8((byte)(value >> 16)); }
        }

        public void AddI32(int value)
        {
            int at = Length;
            Reserve(4);
            SetI32(at, value);
        }

        public void AddI64(long value)
        {
            int at = Length;
            Reserve(8);

            Span<byte> span = _data.AsSpan(at, 8);
            if (_big) BinaryPrimitives.WriteInt64BigEndian(span, value);
            else BinaryPrimitives.WriteInt64LittleEndian(span, value);
        }

        public void SetU8(int at, byte value) => _data[at] = value;

        public void SetU16(int at, ushort value)
        {
            Span<byte> span = _data.AsSpan(at, 2);
            if (_big) BinaryPrimitives.WriteUInt16BigEndian(span, value);
            else BinaryPrimitives.WriteUInt16LittleEndian(span, value);
        }

        public void SetI32(int at, int value)
        {
            Span<byte> span = _data.AsSpan(at, 4);
            if (_big) BinaryPrimitives.WriteInt32BigEndian(span, value);
            else BinaryPrimitives.WriteInt32LittleEndian(span, value);
        }

        public byte[] ToArray() => _data.AsSpan(0, Length).ToArray();

        private void Ensure(int more)
        {
            if (Length + more <= _data.Length) return;

            int size = _data.Length;
            while (size < Length + more) size *= 2;

            System.Array.Resize(ref _data, size);
        }
    }
}
