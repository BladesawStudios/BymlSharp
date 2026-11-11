using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace BymlSharp;

internal static partial class BymlWriter
{
    private sealed class Buffer(bool bigEndian) : IDisposable
    {
        private byte[] _data = ArrayPool<byte>.Shared.Rent(1 << 16);
        private readonly bool _big = bigEndian;

        public int Length { get; private set; }

        public void Dispose()
        {
            ArrayPool<byte>.Shared.Return(_data);
            _data = [];
        }

        public void Reserve(int count)
        {
            Ensure(count);
            _data.AsSpan(Length, count).Clear();
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

        public void AddUtf8(string value)
        {
            int count = Encoding.UTF8.GetByteCount(value);
            Ensure(count);
            Encoding.UTF8.GetBytes(value, _data.AsSpan(Length, count));
            Length += count;
        }

        public void AddBytes(ReadOnlySpan<byte> value)
        {
            Ensure(value.Length);
            value.CopyTo(_data.AsSpan(Length));
            Length += value.Length;
        }

        public void AddU24(int value)
        {
            Ensure(3);
            Span<byte> span = _data.AsSpan(Length, 3);
            if (_big) { span[0] = (byte)(value >> 16); span[1] = (byte)(value >> 8); span[2] = (byte)value; }
            else { span[0] = (byte)value; span[1] = (byte)(value >> 8); span[2] = (byte)(value >> 16); }
            Length += 3;
        }

        public void AddI32(int value)
        {
            Ensure(4);
            Span<byte> span = _data.AsSpan(Length, 4);
            if (_big) BinaryPrimitives.WriteInt32BigEndian(span, value);
            else BinaryPrimitives.WriteInt32LittleEndian(span, value);
            Length += 4;
        }

        public void AddI64(long value)
        {
            Ensure(8);
            Span<byte> span = _data.AsSpan(Length, 8);
            if (_big) BinaryPrimitives.WriteInt64BigEndian(span, value);
            else BinaryPrimitives.WriteInt64LittleEndian(span, value);
            Length += 8;
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

            byte[] larger = ArrayPool<byte>.Shared.Rent(Math.Max(_data.Length * 2, Length + more));
            _data.AsSpan(0, Length).CopyTo(larger);
            ArrayPool<byte>.Shared.Return(_data);
            _data = larger;
        }
    }
}
