namespace BymlSharp;

internal static partial class BymlWriter
{
    private sealed record Placement(
        Dictionary<string, int> Keys, Dictionary<string, int> Strings, string[] StringTable,
        Dictionary<Byml, int> Values, Dictionary<Byml, int> Placed,
        List<(int Patch, Byml Node)> Staged, MapLayout Layout);

    // Maps written one after another often share a key sequence, string instances and all, so the sorted
    // layout of the last map is kept and reused when the next one's keys are the very same strings.
    private sealed class MapLayout
    {
        public int Count = -1;
        public string[] SeenKeys = [];
        public int[] KeyIndexBySlot = [];
        public int[] EntryAtSlot = [];
        public int[] SlotOfEntry = [];

        // The map being written: its keys and children in dictionary order.
        public string[] Current = [];
        public Byml[] Children = [];

        public void EnsureCapacity(int count)
        {
            if (Current.Length >= count) return;
            int size = Math.Max(count, Current.Length * 2);
            Current = new string[size];
            Children = new Byml[size];
        }
    }

    // Larger scratch buffers go on the heap, so a hostile document can't overflow the stack.
    private const int StackLimit = 128;

    private static void WriteChild(Buffer buffer, Placement at, Byml child, int patchAt)
    {
        if (Inline(buffer, child, at.StringTable, at.Strings, patchAt)) return;
        if (child.IsContainer) at.Staged.Add((patchAt, child));
        else buffer.SetI32(patchAt, at.Values[child]);
    }

    // Works out where each of the map's entries goes: sorted by key, which the key table's order also is.
    private static void Reorder(Placement at, Dictionary<string, Byml> map, int count)
    {
        if (at.Layout.KeyIndexBySlot.Length < count)
        {
            int size = Math.Max(count, at.Layout.KeyIndexBySlot.Length * 2);
            at.Layout.SeenKeys = new string[size];
            at.Layout.KeyIndexBySlot = new int[size];
            at.Layout.EntryAtSlot = new int[size];
            at.Layout.SlotOfEntry = new int[size];
        }

        Span<ulong> order = count <= StackLimit ? stackalloc ulong[count] : new ulong[count];
        for (int i = 0; i < count; i++)
        {
            at.Layout.SeenKeys[i] = at.Layout.Current[i];
            order[i] = ((ulong)at.Keys[at.Layout.Current[i]] << 32) | (uint)i;
        }
        order.Sort();

        for (int slot = 0; slot < count; slot++)
        {
            int entry = (int)(uint)order[slot];
            at.Layout.KeyIndexBySlot[slot] = (int)(order[slot] >> 32);
            at.Layout.EntryAtSlot[slot] = entry;
            at.Layout.SlotOfEntry[entry] = slot;
        }

        at.Layout.Count = count;
    }

    private static void WriteContainer(Buffer buffer, Byml node, Placement at)
    {
        // Children that are containers wait on a shared stack, each parent owning the run it pushed.
        int begin = at.Staged.Count;

        switch (node.Type)
        {
            case BymlType.Map:
            {
                Dictionary<string, Byml> map = node.MapEntries;
                int count = map.Count;

                at.Layout.EnsureCapacity(count);
                string[] names = at.Layout.Current;
                Byml[] children = at.Layout.Children;

                bool same = count == at.Layout.Count;
                int index = 0;
                foreach ((string key, Byml child) in map)
                {
                    same &= ReferenceEquals(key, at.Layout.SeenKeys.Length > index ? at.Layout.SeenKeys[index] : null);
                    names[index] = key;
                    children[index++] = child;
                }

                if (!same) Reorder(at, map, count);

                AddHeader(buffer, BymlType.Map, count);
                int first = buffer.Length;
                for (int slot = 0; slot < count; slot++)
                {
                    buffer.AddU24(at.Layout.KeyIndexBySlot[slot]);
                    buffer.AddU8((byte)children[at.Layout.EntryAtSlot[slot]].Type);
                    buffer.AddI32(0);
                }

                for (int entry = 0; entry < count; entry++)
                    WriteChild(buffer, at, children[entry], first + at.Layout.SlotOfEntry[entry] * 8 + 4);

                Array.Clear(children, 0, count);
                break;
            }

            case BymlType.Array:
            {
                List<Byml> items = node.ArrayItems;
                AddHeader(buffer, BymlType.Array, items.Count);

                for (int i = 0; i < items.Count; i++) buffer.AddU8((byte)items[i].Type);
                buffer.Align(4);

                for (int slot = 0; slot < items.Count; slot++)
                {
                    int patch = buffer.Length;
                    buffer.AddI32(0);
                    WriteChild(buffer, at, items[slot], patch);
                }
                break;
            }

            case BymlType.HashMap32 or BymlType.HashMap64:
            {
                bool wide = node.Type is BymlType.HashMap64;

                Dictionary<ulong, Byml> hashMap = node.HashEntries;
                ulong[] hashes = new ulong[hashMap.Count];
                Byml[] children = new Byml[hashMap.Count];
                hashMap.Keys.CopyTo(hashes, 0);
                hashMap.Values.CopyTo(children, 0);
                Array.Sort(hashes, children);

                AddHeader(buffer, node.Type, hashes.Length);

                for (int slot = 0; slot < hashes.Length; slot++)
                {
                    if (wide) buffer.AddI64(unchecked((long)hashes[slot]));
                    else buffer.AddI32(unchecked((int)(uint)hashes[slot]));

                    int patch = buffer.Length;
                    buffer.AddI32(0);
                    WriteChild(buffer, at, children[slot], patch);
                }

                foreach (Byml child in children) buffer.AddU8((byte)child.Type);
                buffer.Align(4);
                break;
            }

            default:
                throw new InvalidOperationException($"{node.Type} is not a container.");
        }

        // Child containers follow their parent, each one's own children before the next sibling.
        List<(int Patch, Byml Node)> staged = at.Staged;
        int end = staged.Count;
        for (int i = begin; i < end; i++)
        {
            (int patch, Byml child) = staged[i];
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
        staged.RemoveRange(begin, end - begin);
    }
}
