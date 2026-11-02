using System.Buffers.Binary;

namespace BymlSharp;

internal static partial class BymlReader
{
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
