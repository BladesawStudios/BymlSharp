namespace BymlSharp;

// Parts of a document's layout that YAML text can't say without anchors: which nodes are one shared instance,
// and the alignment of binary blobs. They are copied from the document the text came from, to wherever the
// edited document still has the same content at the same path.
internal static class BymlSharing
{
    public static void Adopt(Byml root, Byml template)
    {
        Dictionary<Byml, List<object[]>> groups = Collect(template);

        foreach ((Byml original, List<object[]> paths) in groups)
        {
            if (original.Type is BymlType.BinaryAligned) AdoptAlignment(root, original, paths);
            if (paths.Count > 1) Unify(root, paths);
        }
    }

    // Every appearance of each node that is shared, or that is an aligned binary, keyed by the node in the template.
    private static Dictionary<Byml, List<object[]>> Collect(Byml template)
    {
        HashSet<Byml> seen = new(ReferenceEqualityComparer.Instance);
        HashSet<Byml> shared = new(ReferenceEqualityComparer.Instance);
        Count(template, seen, shared);

        Dictionary<Byml, List<object[]>> groups = new(ReferenceEqualityComparer.Instance);
        HashSet<Byml> descended = new(ReferenceEqualityComparer.Instance);
        Walk(template, [], shared, groups, descended);
        return groups;
    }

    private static void Count(Byml node, HashSet<Byml> seen, HashSet<Byml> shared)
    {
        if (BymlWriter.IsInline(node.Type)) return;
        if (!seen.Add(node))
        {
            shared.Add(node);
            return;
        }
        if (node.IsContainer)
            foreach (Byml child in BymlWriter.PlacementOrder(node)) Count(child, seen, shared);
    }

    private static void Walk(Byml node, List<object> path, HashSet<Byml> shared, Dictionary<Byml, List<object[]>> groups, HashSet<Byml> descended)
    {
        if (BymlWriter.IsInline(node.Type)) return;

        if (shared.Contains(node) || node.Type is BymlType.BinaryAligned)
        {
            if (!groups.TryGetValue(node, out List<object[]>? paths)) groups[node] = paths = [];
            paths.Add([.. path]);
        }

        // A node that appears again has the same children, so they are only walked once.
        if (!node.IsContainer || !descended.Add(node)) return;

        switch (node.Type)
        {
            case BymlType.Map:
                foreach (var (key, child) in node.AsMap)
                {
                    path.Add(key);
                    Walk(child, path, shared, groups, descended);
                    path.RemoveAt(path.Count - 1);
                }
                break;
            case BymlType.Array:
                for (int i = 0; i < node.AsArray.Count; i++)
                {
                    path.Add(i);
                    Walk(node.AsArray[i], path, shared, groups, descended);
                    path.RemoveAt(path.Count - 1);
                }
                break;
            default:
                foreach (var (key, child) in node.AsHashMap)
                {
                    path.Add(key);
                    Walk(child, path, shared, groups, descended);
                    path.RemoveAt(path.Count - 1);
                }
                break;
        }
    }

    private static void Unify(Byml root, List<object[]> paths)
    {
        Byml? canonical = null;
        foreach (object[] path in paths)
        {
            if (!Resolve(root, path, out Byml? parent, out object? key, out Byml? node)) continue;

            if (canonical is null) canonical = node;
            else if (!ReferenceEquals(node, canonical) && BymlWriter.ContentComparer.Instance.Equals(canonical, node)) Replace(parent!, key!, canonical);
        }
    }

    private static void AdoptAlignment(Byml root, Byml original, List<object[]> paths)
    {
        foreach (object[] path in paths)
        {
            if (!Resolve(root, path, out Byml? parent, out object? key, out Byml? node)) continue;
            if (node!.Type is BymlType.Binary && node.Binary.AsSpan().SequenceEqual(original.Binary))
                Replace(parent!, key!, Byml.From(node.Binary, original.BinaryAlignment));
        }
    }

    private static bool Resolve(Byml root, object[] path, out Byml? parent, out object? key, out Byml? node)
    {
        parent = null;
        key = null;
        node = root;

        foreach (object step in path)
        {
            parent = node;
            key = step;
            node = (step, parent!.Type) switch
            {
                (string name, BymlType.Map) => parent.AsMap.TryGetValue(name, out Byml? child) ? child : null,
                (int index, BymlType.Array) => index < parent.AsArray.Count ? parent.AsArray[index] : null,
                (ulong hash, BymlType.HashMap32 or BymlType.HashMap64) => parent.AsHashMap.TryGetValue(hash, out Byml? child) ? child : null,
                _ => null,
            };
            if (node is null) return false;
        }

        return parent is not null;
    }

    private static void Replace(Byml parent, object key, Byml node)
    {
        switch (key)
        {
            case string name: parent.AsMap[name] = node; break;
            case int index: parent.AsArray[index] = node; break;
            case ulong hash: parent.AsHashMap[hash] = node; break;
        }
    }
}
