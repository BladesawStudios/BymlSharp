using System.Buffers.Binary;
using System.Text;

namespace BymlSharp;

/// <summary>
/// Parses the binary form into <see cref="Byml"/> nodes.
/// </summary>
/// <remarks>
/// The layout is a header naming three offsets - a table of key names, a table of string
/// values, and the root node - and then nodes that reference each other by absolute offset.
/// A node is a type byte and four bytes of payload: small values sit in the payload itself,
/// while anything larger than four bytes, containers included, puts an offset there instead.
/// </remarks>
internal static class BymlReader
{
    private const int HeaderSize = 0x10;
    private const int ContainerHeaderSize = 4;

    public static Byml Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize) throw new InvalidDataException("Too short to be a BYML document.");

        // The magic is the one field whose byte order tells you the rest: "YB" little, "BY" big.
        bool big = data[0] == (byte)'B' && data[1] == (byte)'Y';
        if (!big && !(data[0] == (byte)'Y' && data[1] == (byte)'B'))
            throw new InvalidDataException("Not a BYML document: expected a YB or BY magic.");

        Cursor c = new(data, big);

        ushort version = c.U16(0x02);
        if (version is < 2 or > 9)
            throw new InvalidDataException($"Unsupported BYML version {version}.");

        int keyTable = c.I32(0x04);
        int stringTable = c.I32(0x08);
        int root = c.I32(0x0C);

        string[] keys = ReadStringTable(c, keyTable);
        string[] strings = ReadStringTable(c, stringTable);

        // A document whose root offset is zero holds nothing at all, which is legal.
        return root == 0 ? Byml.Null : ReadNode(c, keys, strings, root, TypeAt(c, root));
    }

    private static BymlType TypeAt(in Cursor c, int offset) => (BymlType)c.U8(offset);

    private static string[] ReadStringTable(in Cursor c, int offset)
    {
        if (offset == 0) return [];

        int count = c.I32(offset) >> 8;
        if (count == 0) return [];

        // Offsets are relative to the container, and there is one more of them than there are
        // strings: the last marks where the final one ends.
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

    private static Byml ReadNode(in Cursor c, string[] keys, string[] strings, int value, BymlType type)
        => type switch
        {
            BymlType.Map => ReadMap(c, keys, strings, value),
            BymlType.Array => ReadArray(c, keys, strings, value),
            BymlType.HashMap32 => ReadHashMap(c, keys, strings, value, wide: false),
            BymlType.HashMap64 => ReadHashMap(c, keys, strings, value, wide: true),

            BymlType.String => Byml.Of(type, value >= 0 && value < strings.Length ? strings[value] : string.Empty),
            BymlType.Binary or BymlType.BinaryAligned => Byml.Of(BymlType.Binary, ReadBinary(c, value, type)),

            // Four bytes or fewer travel in the node itself.
            BymlType.Bool => Byml.Of(type, value != 0),
            BymlType.Int => Byml.Of(type, value),
            BymlType.UInt => Byml.Of(type, unchecked((uint)value)),
            BymlType.Float => Byml.Of(type, BitConverter.Int32BitsToSingle(value)),

            // Anything wider is stored elsewhere and the node holds its offset.
            BymlType.Int64 => Byml.Of(type, c.I64(value)),
            BymlType.UInt64 => Byml.Of(type, unchecked((ulong)c.I64(value))),
            BymlType.Double => Byml.Of(type, BitConverter.Int64BitsToDouble(c.I64(value))),

            BymlType.Null or BymlType.None => Byml.Null,
            _ => throw new InvalidDataException($"Unsupported BYML node type 0x{(byte)type:X2}."),
        };

    private static byte[] ReadBinary(in Cursor c, int offset, BymlType type)
    {
        int size = c.I32(offset);

        // The aligned kind spends another four bytes saying what it is aligned to.
        int start = offset + (type == BymlType.BinaryAligned ? 8 : 4);
        return c.Slice(start, size).ToArray();
    }

    private static Byml ReadMap(in Cursor c, string[] keys, string[] strings, int offset)
    {
        int count = c.I32(offset) >> 8;
        Dictionary<string, Byml> map = new(count, StringComparer.Ordinal);

        for (int i = 0; i < count; i++)
        {
            // Eight bytes an entry: three bytes of key index, the type, then the payload.
            int entry = offset + ContainerHeaderSize + i * 8;
            int packed = c.I32(entry);

            int keyIndex = packed & 0xFFFFFF;
            BymlType type = (BymlType)(byte)(packed >> 24);

            string key = keyIndex >= 0 && keyIndex < keys.Length ? keys[keyIndex] : $"<{keyIndex}>";
            map[key] = ReadNode(c, keys, strings, c.I32(entry + 4), type);
        }

        return Byml.Of(BymlType.Map, map);
    }

    private static Byml ReadArray(in Cursor c, string[] keys, string[] strings, int offset)
    {
        int count = c.I32(offset) >> 8;
        List<Byml> array = new(count);

        // The types come first, one byte each, then padding out to four so the payloads that
        // follow stay aligned.
        int types = offset + ContainerHeaderSize;
        int values = types + ((count + 3) & ~3);

        for (int i = 0; i < count; i++)
            array.Add(ReadNode(c, keys, strings, c.I32(values + i * 4), (BymlType)c.U8(types + i)));

        return Byml.Of(BymlType.Array, array);
    }

    private static Byml ReadHashMap(in Cursor c, string[] keys, string[] strings, int offset, bool wide)
    {
        int count = c.I32(offset) >> 8;
        int entrySize = wide ? 0xC : 0x8;

        Dictionary<ulong, Byml> map = new(count);

        int entries = offset + ContainerHeaderSize;
        int types = entries + entrySize * count;

        for (int i = 0; i < count; i++)
        {
            int entry = entries + i * entrySize;
            ulong hash = wide ? unchecked((ulong)c.I64(entry)) : c.U32(entry);
            int value = c.I32(entry + (wide ? 8 : 4));

            map[hash] = ReadNode(c, keys, strings, value, (BymlType)c.U8(types + i));
        }

        return Byml.Of(wide ? BymlType.HashMap64 : BymlType.HashMap32, map);
    }

    /// <summary>Bounds-checked reads in the document's byte order.</summary>
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
