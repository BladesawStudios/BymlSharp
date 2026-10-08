using System.Buffers.Binary;
using System.Text;

namespace BymlSharp;

internal static class BymlWriter
{
    private const int HeaderSize = 0x10;
    private const int ContainerHeaderSize = 4;

    public static byte[] Write(Byml root, int version, bool bigEndian, bool shareIdenticalContainers = false)
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

        // A container that appears in several places is written once; with shareIdenticalContainers,
        // so is any container equal to one already written.
        IEqualityComparer<Byml> sharing = shareIdenticalContainers ? ContentComparer.Instance : ReferenceEqualityComparer.Instance;

        int rootOffset = 0;
        if (root.IsContainer)
        {
            buffer.Align(4);
            Dictionary<Byml, int> values = new(ReferenceEqualityComparer.Instance);
            WriteValues(buffer, root, values, new(sharing));

            buffer.Align(4);
            rootOffset = buffer.Length;
            WriteContainer(buffer, root, new Placement(keyIndex, stringIndex, values, new(sharing)));
        }
        else if (!root.IsNull)
        {
            throw new InvalidOperationException(
                $"A BYML document's root must be a container or null, not {root.Type}.");
        }

        // "YB" little, "BY" big.
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

    // Every appearance of a value is written, and all of them point at the last copy: what Nintendo's
    // writer does with a value node shared between several places.
    private static void WriteValues(Buffer buffer, Byml node, Dictionary<Byml, int> offsets, HashSet<Byml> visited)
    {
        if (!visited.Add(node)) return;

        foreach (Byml child in PlacementOrder(node))
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
    }

    private sealed record Placement(
        Dictionary<string, int> Keys, Dictionary<string, int> Strings,
        Dictionary<Byml, int> Values, Dictionary<Byml, int> Placed);

    private static void WriteContainer(Buffer buffer, Byml node, Placement at)
    {
        List<(int Patch, Byml Node)> staged = [];
        Dictionary<string, int> keys = at.Keys;

        void WriteChild(Byml child, int patchAt)
        {
            if (Inline(buffer, child, at.Strings, patchAt)) return;
            if (child.IsContainer) staged.Add((patchAt, child));
            else buffer.SetI32(patchAt, at.Values[child]);
        }

        switch (node.Type)
        {
            case BymlType.Map:
            {
                var entries = node.AsMap.OrderBy(e => e.Key, StringComparer.Ordinal).ToList();
                AddHeader(buffer, BymlType.Map, entries.Count);

                Dictionary<string, int> patches = new(entries.Count, StringComparer.Ordinal);
                foreach ((string key, Byml child) in entries)
                {
                    buffer.AddU24(keys[key]);
                    buffer.AddU8((byte)child.Type);
                    patches[key] = buffer.Length;
                    buffer.AddI32(0);
                }

                foreach ((string key, Byml child) in node.AsMap)
                    WriteChild(child, patches[key]);
                break;
            }

            case BymlType.Array:
            {
                IList<Byml> items = node.AsArray;
                AddHeader(buffer, BymlType.Array, items.Count);

                foreach (Byml child in items) buffer.AddU8((byte)child.Type);
                buffer.Align(4);

                for (int slot = 0; slot < items.Count; slot++)
                {
                    int patch = buffer.Length;
                    buffer.AddI32(0);
                    WriteChild(items[slot], patch);
                }
                break;
            }

            case BymlType.HashMap32 or BymlType.HashMap64:
            {
                bool wide = node.Type is BymlType.HashMap64;

                var entries = node.AsHashMap.OrderBy(e => e.Key).ToList();
                AddHeader(buffer, node.Type, entries.Count);

                for (int slot = 0; slot < entries.Count; slot++)
                {
                    (ulong hash, Byml child) = entries[slot];
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

        // Child containers follow their parent, each one's own children before the next sibling.
        foreach ((int patch, Byml child) in staged)
        {
            if (at.Placed.TryGetValue(child, out int existing))
            {
                buffer.SetI32(patch, existing);
                continue;
            }

            buffer.Align(4);
            buffer.SetI32(patch, buffer.Length);
            at.Placed.Add(child, buffer.Length);
            WriteContainer(buffer, child, at);
        }
    }

    private sealed class ContentComparer : IEqualityComparer<Byml>
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

    internal static bool IsInline(BymlType type) => type is BymlType.String or BymlType.Bool or BymlType.Int
        or BymlType.UInt or BymlType.Float or BymlType.Null or BymlType.None;

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

        public void AlignPayload(int skip, int to)
        {
            if (to <= 1) return;
            int payload = Length + skip;
            Reserve((payload + to - 1) / to * to - payload);
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
