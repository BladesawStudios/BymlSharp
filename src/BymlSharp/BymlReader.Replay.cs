using System.Buffers.Binary;

namespace BymlSharp;

internal static partial class BymlReader
{
    // The value section holds every out-of-line value in the order the writer meets them: map entries in
    // insertion order, each container once. The order the offsets suggest is usually that order; where a
    // value is shared the pointers name its last copy instead, so walk the section the way the writer
    // fills it and take, at each step, the first entry whose values are the bytes found there.
    private sealed class Replay(bool big)
    {
        // Each container visited takes the next stamp. Backing out of a failed attempt kills the stamps issued
        // since it began instead of unmarking those containers one by one, and visits are rarely backed out.
        private int _stamp;
        private List<(int After, int Through)>? _undone;
        private int _position;

        private bool IsVisited(Byml node)
        {
            int stamp = node.VisitStamp;
            if (stamp == 0) return false;
            if (_undone is null) return true;
            foreach ((int after, int through) in _undone)
                if (stamp > after && stamp <= through) return false;
            return true;
        }

        public bool Walk(ReadOnlySpan<byte> data, Byml root, int start)
        {
            _position = start;
            return Container(data, root);
        }

        private bool Container(ReadOnlySpan<byte> data, Byml node)
        {
            if (IsVisited(node)) return true;
            node.VisitStamp = ++_stamp;

            if (node.IsMap) return Map(data, node);
            if (node.IsArray)
            {
                List<Byml> items = (List<Byml>)node.AsArray;
                for (int i = 0; i < items.Count; i++)
                    if (!Child(data, items[i])) return false;
                return true;
            }

            foreach (Byml child in BymlWriter.PlacementOrder(node))
                if (!Child(data, child)) return false;
            return true;
        }

        private void Rewind(int position, int stamp)
        {
            _position = position;
            if (stamp != _stamp) (_undone ??= []).Add((stamp, _stamp));
        }

        private bool Child(ReadOnlySpan<byte> data, Byml child)
            => child.IsContainer ? Container(data, child) : BymlWriter.IsInline(child.Type) || Value(data, child);

        private bool Map(ReadOnlySpan<byte> data, Byml node)
        {
            Dictionary<string, Byml> map = (Dictionary<string, Byml>)node.AsMap;

            // Nearly every map is already in the order it was written, so try that order first and only
            // fall back to searching for one when a value isn't where the walk expects it.
            (int start, int started) = (_position, _stamp);
            bool ordered = true;
            foreach (KeyValuePair<string, Byml> entry in map)
            {
                if (BymlWriter.IsInline(entry.Value.Type)) continue;
                if (Child(data, entry.Value)) continue;
                ordered = false;
                break;
            }

            if (ordered) return true;
            Rewind(start, started);

            KeyValuePair<string, Byml>[] entries = new KeyValuePair<string, Byml>[map.Count];
            Span<int> remaining = map.Count <= StackLimit ? stackalloc int[map.Count] : new int[map.Count];
            Span<int> chosen = map.Count <= StackLimit ? stackalloc int[map.Count] : new int[map.Count];

            {
                ((ICollection<KeyValuePair<string, Byml>>)map).CopyTo(entries, 0);

                int count = 0;
                for (int i = 0; i < map.Count; i++)
                    if (!BymlWriter.IsInline(entries[i].Value.Type)) remaining[count++] = i;

                int left = count, picked = 0;
                bool inOrder = true;
                while (left > 0)
                {
                    int pick = -1;
                    for (int i = 0; i < left; i++)
                    {
                        (int position, int visits) = (_position, _stamp);
                        if (Child(data, entries[remaining[i]].Value))
                        {
                            pick = i;
                            break;
                        }

                        Rewind(position, visits);
                    }

                    if (pick < 0) return false;
                    if (pick != 0) inOrder = false;
                    chosen[picked++] = remaining[pick];
                    remaining[(pick + 1)..left].CopyTo(remaining[pick..]);
                    left--;
                }

                if (inOrder) return true;

                // Slots are the positions of the out-of-line entries; they receive the chosen entries in order.
                var reordered = new KeyValuePair<string, Byml>[map.Count];
                Array.Copy(entries, reordered, map.Count);
                int slot = 0;
                for (int i = 0; i < map.Count; i++)
                    if (!BymlWriter.IsInline(entries[i].Value.Type)) reordered[i] = entries[chosen[slot++]];

                map.Clear();
                for (int i = 0; i < reordered.Length; i++) map.Add(reordered[i].Key, reordered[i].Value);
                return true;
            }
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
}
