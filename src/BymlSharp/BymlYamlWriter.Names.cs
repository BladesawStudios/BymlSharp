using System.Globalization;

namespace BymlSharp;

internal static partial class BymlYamlWriter
{
    private sealed class Names
    {
        private readonly HashSet<Byml> _shared = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<Byml, string> _names = new(ReferenceEqualityComparer.Instance);

        public Names(Byml root) => Count(root, new HashSet<Byml>(ReferenceEqualityComparer.Instance));

        private void Count(Byml node, HashSet<Byml> seen)
        {
            if (BymlWriter.IsInline(node.Type)) return;
            if (!seen.Add(node))
            {
                _shared.Add(node);
                return;
            }

            switch (node.Type)
            {
                case BymlType.Map:
                    foreach (Byml child in node.AsMap.Values) Count(child, seen);
                    break;
                case BymlType.Array:
                    foreach (Byml child in node.AsArray) Count(child, seen);
                    break;
                case BymlType.HashMap32 or BymlType.HashMap64:
                    foreach (Byml child in node.AsHashMap.Values) Count(child, seen);
                    break;
            }
        }

        public bool Claim(Byml node, out string? anchor, out string? alias)
        {
            anchor = alias = null;
            if (!_shared.Contains(node)) return false;

            if (_names.TryGetValue(node, out string? name))
            {
                alias = name;
                return true;
            }

            _names[node] = anchor = (_names.Count + 1).ToString(CultureInfo.InvariantCulture);
            return true;
        }
    }
}
