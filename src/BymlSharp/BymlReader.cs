using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace BymlSharp;

internal static partial class BymlReader
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

        Tables t = new(ReadStringTable(c, keyTable), ReadStringTable(c, stringTable), data.Length);

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

    private sealed class Tables(string[] keys, string[] strings, int length)
    {
        private const int PageBits = 8;
        private const int PageMask = (1 << PageBits) - 1;

        public string[] Keys { get; } = keys;
        public string[] Strings { get; } = strings;

        private readonly Byml?[]?[] _pages = new Byml?[]?[(length >> (2 + PageBits)) + 1];
        private Dictionary<long, Byml>? _overflow;

        public bool TryGetNode(int offset, BymlType type, [NotNullWhen(true)] out Byml? node)
        {
            int slot = offset >> 2;
            if ((offset & 3) == 0 && (uint)(slot >> PageBits) < (uint)_pages.Length
                && _pages[slot >> PageBits] is { } page && page[slot & PageMask] is { } hit && hit.Type == type)
            {
                node = hit;
                return true;
            }

            node = null;
            return _overflow is not null && _overflow.TryGetValue(NodeKey(offset, type), out node);
        }

        public void SetNode(int offset, BymlType type, Byml node)
        {
            int slot = offset >> 2;
            if ((offset & 3) == 0 && (uint)(slot >> PageBits) < (uint)_pages.Length)
            {
                Byml?[] page = _pages[slot >> PageBits] ??= new Byml?[1 << PageBits];
                if (page[slot & PageMask] is null)
                {
                    page[slot & PageMask] = node;
                    return;
                }
            }
            (_overflow ??= [])[NodeKey(offset, type)] = node;
        }

        public int FirstValue { get; set; } = int.MaxValue;

        public int LastNodeFirstValue { get; set; } = int.MaxValue;
    }

    private static long NodeKey(int offset, BymlType type) => ((long)offset << 8) | (byte)type;

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
            if (t.TryGetNode(value, type, out Byml? shared))
            {
                t.LastNodeFirstValue = int.MaxValue;
                return shared;
            }

            int outer = t.FirstValue;
            t.FirstValue = int.MaxValue;
            Byml node = ReadValue(c, t, value, type);
            t.SetNode(value, type, node);
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
        if (!t.TryGetNode(value, type, out Byml? same))
        {
            same = ReadValue(c, t, value, type);
            t.SetNode(value, type, same);
        }
        return same;
    }

    private static Byml ReadValue(in Cursor c, Tables t, int value, BymlType type)
        => type switch
        {
            BymlType.Map => ReadMap(c, t, value),
            BymlType.Array => ReadArray(c, t, value),
            BymlType.HashMap32 => ReadHashMap(c, t, value, wide: false),
            BymlType.HashMap64 => ReadHashMap(c, t, value, wide: true),

            BymlType.String => value >= 0 && value < t.Strings.Length ? Byml.OfString(t.Strings[value], value) : Byml.Of(type, string.Empty),
            BymlType.Binary or BymlType.BinaryAligned => ReadBinary(c, value, type),

            BymlType.Bool => Byml.OfBool(value != 0),
            BymlType.Int => Byml.OfInt(value),
            BymlType.UInt => Byml.OfUInt(unchecked((uint)value)),
            BymlType.Float => Byml.OfFloat(BitConverter.Int32BitsToSingle(value)),

            BymlType.Int64 => Byml.OfInt64(c.I64(value)),
            BymlType.UInt64 => Byml.OfUInt64(unchecked((ulong)c.I64(value))),
            BymlType.Double => Byml.OfDouble(BitConverter.Int64BitsToDouble(c.I64(value))),

            BymlType.Null or BymlType.None => Byml.Null,
            _ => throw new InvalidDataException($"Unsupported BYML node type 0x{(byte)type:X2}."),
        };

    private static Byml ReadBinary(in Cursor c, int offset, BymlType type)
    {
        int size = c.I32(offset);

        if (type is BymlType.BinaryAligned)
            return Byml.OfAligned(c.Slice(offset + 8, size).ToArray(), c.I32(offset + 4));

        return Byml.Of(BymlType.Binary, c.Slice(offset + 4, size).ToArray());
    }

}
