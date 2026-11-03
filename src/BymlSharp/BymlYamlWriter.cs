using System.Buffers;
using System.Globalization;

namespace BymlSharp;

// Writes the text YamlDotNet's emitter would for a BYML tree, without building the events. Strings that need
// more than the plainest quoting rules are rendered by YamlDotNet itself (see Quote) and anything the layout
// below doesn't cover makes TryWrite return null, so the caller can use the reference emitter instead.
internal static partial class BymlYamlWriter
{
    private const int SimpleKeyLimit = 128;

    private sealed class Unsupported : Exception;

    public static string? TryWrite(Byml root, bool anchors)
    {
        Writer writer = new(anchors ? new Names(root) : null);
        try
        {
            writer.Document(root);
            return writer.ToString();
        }
        catch (Unsupported)
        {
            return null;
        }
        finally
        {
            writer.Dispose();
        }
    }

    private enum Place { Root, MapValue, SeqItem }

    private sealed partial class Writer(Names? names) : IDisposable
    {
        private static readonly string NewLine = Environment.NewLine;

        private char[] _buffer = ArrayPool<char>.Shared.Rent(1 << 14);
        private int _length;

        public void Dispose() => ArrayPool<char>.Shared.Return(_buffer);

        public override string ToString() => new(_buffer, 0, _length);

        private Span<char> Reserve(int count)
        {
            if (_length + count > _buffer.Length)
            {
                char[] larger = ArrayPool<char>.Shared.Rent(Math.Max(_buffer.Length * 2, _length + count));
                _buffer.AsSpan(0, _length).CopyTo(larger);
                ArrayPool<char>.Shared.Return(_buffer);
                _buffer = larger;
            }
            return _buffer.AsSpan(_length, count);
        }

        private void Add(char c)
        {
            if (_length == _buffer.Length) Reserve(1);
            _buffer[_length++] = c;
        }

        private void Add(ReadOnlySpan<char> text)
        {
            text.CopyTo(Reserve(text.Length));
            _length += text.Length;
        }

        private void Add(string text) => Add(text.AsSpan());

        private void Spaces(int count)
        {
            Reserve(count).Fill(' ');
            _length += count;
        }

        private void EndLine() => Add(NewLine);

        public void Document(Byml root)
        {
            Value(root, 0, Place.Root);
        }

        // The node sits after "key:" or "-" (or opens the document); `indent` is the column of that key or dash.
        private void Value(Byml node, int indent, Place place)
        {
            string? anchor = null;
            if (names is not null && !BymlWriter.IsInline(node.Type) && names.Claim(node, out anchor, out string? alias) && alias is not null)
            {
                Lead(place);
                Add('*');
                Add(alias);
                EndLine();
                return;
            }

            if (!node.IsContainer)
            {
                Lead(place);
                Scalar(node, anchor);
                EndLine();
                return;
            }

            string? tag = node.Type switch
            {
                BymlType.HashMap32 => "!h32",
                BymlType.HashMap64 => "!h64",
                _ => null,
            };

            if (IsFlow(node))
            {
                Lead(place);
                Properties(anchor, tag);
                if (anchor is not null || tag is not null) Add(' ');
                FlowText(node);
                EndLine();
                return;
            }

            bool hasProperties = anchor is not null || tag is not null;
            if (place == Place.Root && !hasProperties)
            {
                Block(node, 0, firstOnLine: false);
                return;
            }

            if (place == Place.SeqItem && !hasProperties)
            {
                // A collection opening a sequence item starts on the dash's line.
                Add(' ');
                Block(node, indent + 2, firstOnLine: true);
                return;
            }

            if (hasProperties)
            {
                Lead(place);
                Properties(anchor, tag);
            }
            EndLine();

            // Sequences under a key sit level with it; everything else is indented two more.
            int childIndent = place switch
            {
                Place.Root => indent,
                Place.SeqItem => indent + 2,
                _ => node.Type is BymlType.Array ? indent : indent + 2,
            };
            Block(node, childIndent, firstOnLine: false);
        }

        // Small collections of plain scalars sit on one line: {X: 1.0, Y: 2.0}. Same rule as the reference emitter.
        private static bool IsFlow(Byml node)
        {
            switch (node.Type)
            {
                case BymlType.Map:
                    Dictionary<string, Byml> map = node.MapEntries;
                    if (map.Count > BymlYamlLegacy.FlowLimit) return false;
                    foreach (Byml child in map.Values) if (!FlowItem(child)) return false;
                    return true;

                case BymlType.Array:
                    List<Byml> items = node.ArrayItems;
                    if (items.Count > BymlYamlLegacy.FlowLimit) return false;
                    foreach (Byml child in items) if (!FlowItem(child)) return false;
                    return true;

                default:
                    Dictionary<ulong, Byml> hashes = node.HashEntries;
                    if (hashes.Count > BymlYamlLegacy.FlowLimit) return false;
                    foreach (Byml child in hashes.Values) if (!FlowItem(child)) return false;
                    return true;
            }
        }

        private static bool FlowItem(Byml child) => !child.IsContainer && child.Type is not (BymlType.Binary or BymlType.BinaryAligned or BymlType.String);

        private void Lead(Place place)
        {
            if (place != Place.Root) Add(' ');
        }

        private void Properties(string? anchor, string? tag)
        {
            if (anchor is not null)
            {
                Add('&');
                Add(anchor);
            }
            if (tag is not null)
            {
                if (anchor is not null) Add(' ');
                Add(tag);
            }
        }

        private void Block(Byml node, int indent, bool firstOnLine)
        {
            bool first = true;
            switch (node.Type)
            {
                case BymlType.Map:
                    foreach ((string key, Byml child) in node.MapEntries)
                    {
                        if (!(first && firstOnLine)) Spaces(indent);
                        first = false;
                        Text(key, isKey: true, flow: false);
                        Add(':');
                        Value(child, indent, Place.MapValue);
                    }
                    break;

                case BymlType.Array:
                    foreach (Byml child in node.ArrayItems)
                    {
                        if (!(first && firstOnLine)) Spaces(indent);
                        first = false;
                        Add('-');
                        Value(child, indent, Place.SeqItem);
                    }
                    break;

                default:
                    foreach ((ulong key, Byml child) in node.HashEntries)
                    {
                        if (!(first && firstOnLine)) Spaces(indent);
                        first = false;
                        HashKey(key);
                        Add(':');
                        Value(child, indent, Place.MapValue);
                    }
                    break;
            }
        }

        private void HashKey(ulong key)
        {
            Add("0x");
            key.TryFormat(Reserve(16), out int written, "X", CultureInfo.InvariantCulture);
            _length += written;
        }

        private void FlowText(Byml node)
        {
            bool first = true;
            switch (node.Type)
            {
                case BymlType.Map:
                    Add('{');
                    foreach ((string key, Byml child) in node.MapEntries)
                    {
                        if (!first) Add(", ");
                        first = false;
                        Text(key, isKey: true, flow: true);
                        Add(": ");
                        FlowValue(child);
                    }
                    Add('}');
                    break;

                case BymlType.Array:
                    Add('[');
                    foreach (Byml child in node.ArrayItems)
                    {
                        if (!first) Add(", ");
                        first = false;
                        FlowValue(child);
                    }
                    Add(']');
                    break;

                default:
                    Add('{');
                    foreach ((ulong key, Byml child) in node.HashEntries)
                    {
                        if (!first) Add(", ");
                        first = false;
                        HashKey(key);
                        Add(": ");
                        FlowValue(child);
                    }
                    Add('}');
                    break;
            }
        }

        private void FlowValue(Byml node)
        {
            string? anchor = null;
            if (names is not null && !BymlWriter.IsInline(node.Type) && names.Claim(node, out anchor, out string? alias) && alias is not null)
            {
                Add('*');
                Add(alias);
                return;
            }
            Scalar(node, anchor);
        }
    }
}
