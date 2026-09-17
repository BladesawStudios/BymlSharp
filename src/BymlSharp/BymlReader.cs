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

        string[] keys = ReadStringTable(c, keyTable);
        string[] strings = ReadStringTable(c, stringTable);

        // A document whose root offset is zero holds nothing at all, which is legal.
        Byml node = root == 0 ? Byml.Null : ReadNode(c, keys, strings, root, TypeAt(c, root));
        return new BymlFile(node, version, big);
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

    private static Byml ReadNode(in Cursor c, string[] keys, string[] strings, int value, BymlType type)
        => type switch
        {
            BymlType.Map => ReadMap(c, keys, strings, value),
            BymlType.Array => ReadArray(c, keys, strings, value),
            BymlType.HashMap32 => ReadHashMap(c, keys, strings, value, wide: false),
            BymlType.HashMap64 => ReadHashMap(c, keys, strings, value, wide: true),

            BymlType.String => Byml.Of(type, value >= 0 && value < strings.Length ? strings[value] : string.Empty),
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

    private static Byml ReadMap(in Cursor c, string[] keys, string[] strings, int offset)
    {
        int count = CountAt(c, offset);
        Dictionary<string, Byml> map = new(count, StringComparer.Ordinal);

        for (int i = 0; i < count; i++)
        {
            int entry = offset + ContainerHeaderSize + i * 8;

            int keyIndex = c.U24(entry);
            BymlType type = (BymlType)c.U8(entry + 3);

            string key = keyIndex >= 0 && keyIndex < keys.Length ? keys[keyIndex] : $"<{keyIndex}>";
            map[key] = ReadNode(c, keys, strings, c.I32(entry + 4), type);
        }

        return Byml.Of(BymlType.Map, map);
    }

    private static Byml ReadArray(in Cursor c, string[] keys, string[] strings, int offset)
    {
        int count = CountAt(c, offset);
        List<Byml> array = new(count);

        int types = offset + ContainerHeaderSize;
        int values = types + ((count + 3) & ~3);

        for (int i = 0; i < count; i++)
            array.Add(ReadNode(c, keys, strings, c.I32(values + i * 4), (BymlType)c.U8(types + i)));

        return Byml.Of(BymlType.Array, array);
    }

    private static Byml ReadHashMap(in Cursor c, string[] keys, string[] strings, int offset, bool wide)
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

            map[hash] = ReadNode(c, keys, strings, value, (BymlType)c.U8(types + i));
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
