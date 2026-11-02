namespace BymlSharp;

// Reads the YAML this library writes and the forms editors keep it in: block and single-line flow collections,
// plain and quoted scalars on one line, anchors, aliases, tags and comments. Anything else throws Unsupported
// and the caller reads it with the YamlDotNet-based reader, which also has the last word on malformed input.
internal static partial class BymlYamlReader
{
    internal sealed class Unsupported : Exception;

    public static Byml Read(string yaml) => new Parser(yaml).Document();

    private struct Line
    {
        public int Start;   // where the content begins; moved right when a "- " or "key: " is read through
        public int End;
        public int Indent;  // column of Start
        public int Base;    // where the line begins in the text
    }

    private enum Place { Root, MapValue, SeqItem }

    private sealed partial class Parser
    {
        private readonly string _text;
        private readonly Line[] _lines;
        private readonly int _count;
        private int _at;

        private Dictionary<string, Byml>? _anchors;
        private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
        private Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> _lookup;

        public Parser(string text)
        {
            _text = text;
            _lookup = _names.GetAlternateLookup<ReadOnlySpan<char>>();

            List<Line> lines = new(Math.Max(16, text.Length / 24));
            int position = text.Length > 0 && text[0] == '﻿' ? 1 : 0;
            while (position < text.Length)
            {
                int newline = text.IndexOf('\n', position);
                int next = newline < 0 ? text.Length : newline + 1;
                int end = newline < 0 ? text.Length : newline;
                if (end > position && text[end - 1] == '\r') end--;

                int start = position;
                while (start < end && text[start] == ' ') start++;

                if (start < end)
                {
                    char c = text[start];
                    if (c == '\t' || c == '\r' || text.AsSpan(start, end - start).Contains('\r')) throw new Unsupported();
                    if (c != '#')
                    {
                        if (start == position && (IsMarker(text, start, end, "---") || IsMarker(text, start, end, "..."))) throw new Unsupported();
                        lines.Add(new Line { Start = start, End = end, Indent = start - position, Base = position });
                    }
                }
                position = next;
            }

            _lines = [.. lines];
            _count = _lines.Length;
        }

        private static bool IsMarker(string text, int start, int end, string marker)
            => end - start >= 3 && string.CompareOrdinal(text, start, marker, 0, 3) == 0 && (end - start == 3 || text[start + 3] == ' ');

        public Byml Document()
        {
            if (_count == 0) return Byml.Null;

            Byml root = Value(_lines[0].Start, Place.Root, -1);
            if (_at != _count) throw new Unsupported();
            return root;
        }

        private bool IsDash(int i, int end) => _text[i] == '-' && (i + 1 == end || _text[i + 1] == ' ');

        private int SkipSpaces(int i, int end)
        {
            while (i < end && _text[i] == ' ') i++;
            return i;
        }

        private bool AtEnd(int i, int end) => i >= end || (_text[i] == '#' && i > 0 && _text[i - 1] == ' ');

        // The value that starts at `i` on the current line and runs on to whatever lines belong to it.
        // Afterwards _at is the first line that doesn't.
        private Byml Value(int i, Place place, int parentIndent)
        {
            int end = _lines[_at].End;
            i = SkipSpaces(i, end);

            if (i < end && !AtEnd(i, end))
            {
                if (place != Place.MapValue && IsDash(i, end))
                {
                    Rebase(i);
                    return Sequence();
                }

                if (IsEntry(i, end))
                {
                    if (place == Place.MapValue) throw new Unsupported();
                    Rebase(i);
                    return Mapping(null);
                }
            }

            string? anchor = null, tag = null;
            while (i < end && (_text[i] == '&' || _text[i] == '!'))
            {
                int stop = i;
                while (stop < end && _text[stop] != ' ') stop++;

                if (_text[i] == '&')
                {
                    if (anchor is not null || stop == i + 1) throw new Unsupported();
                    anchor = _text.Substring(i + 1, stop - i - 1);
                }
                else
                {
                    if (tag is not null) throw new Unsupported();
                    tag = Tag(_text.AsSpan(i, stop - i));
                }
                i = SkipSpaces(stop, end);
            }

            Byml node;
            if (i >= end || AtEnd(i, end))
            {
                _at++;
                bool child = _at < _count && (_lines[_at].Indent > parentIndent
                    || (place == Place.MapValue && _lines[_at].Indent == parentIndent && IsDash(_lines[_at].Start, _lines[_at].End)));
                node = child ? Block(tag) : Typed(tag, quoted: false, "");
            }
            else
            {
                node = Inline(ref i, end, tag, anchor is not null);
                if (!AtEnd(SkipSpaces(i, end), end)) throw new Unsupported();
                _at++;
            }

            if (anchor is not null) (_anchors ??= new(StringComparer.Ordinal))[anchor] = node;
            return node;
        }

        // The rest of the line, from `i`, becomes a line of its own: how "- - 1" and "- key: 1" are read.
        private void Rebase(int i)
        {
            ref Line line = ref _lines[_at];
            line.Start = i;
            line.Indent = i - line.Base;
        }

        private Byml Block(string? tag)
        {
            ref Line line = ref _lines[_at];
            if (IsDash(line.Start, line.End))
            {
                if (tag is not null) throw new Unsupported();
                return Sequence();
            }
            if (IsEntry(line.Start, line.End)) return Mapping(tag);
            throw new Unsupported();
        }

        private Byml Sequence()
        {
            int indent = _lines[_at].Indent;
            List<Byml> items = [];

            while (true)
            {
                ref Line line = ref _lines[_at];
                items.Add(Value(line.Start + 1, Place.SeqItem, indent));

                if (_at >= _count) break;
                ref Line next = ref _lines[_at];
                if (next.Indent < indent) break;
                if (next.Indent > indent) throw new Unsupported();
                if (!IsDash(next.Start, next.End)) break;
            }
            return Byml.Array(items);
        }

        private Byml Mapping(string? tag)
        {
            int indent = _lines[_at].Indent;
            bool hash = tag is "!h32" or "!h64";
            if (tag is not null && !hash) throw new Unsupported();

            Dictionary<string, Byml>? map = hash ? null : new(8, StringComparer.Ordinal);
            Dictionary<ulong, Byml>? hashes = hash ? [] : null;

            while (true)
            {
                ref Line line = ref _lines[_at];
                int i = line.Start;
                string key = Key(ref i, line.End);

                Byml value = Value(i + 1, Place.MapValue, indent);
                if (hash) hashes![BymlYamlLegacy.Integer(key) ?? throw new Unsupported()] = value;
                else map![key] = value;

                if (_at >= _count) break;
                ref Line next = ref _lines[_at];
                if (next.Indent < indent) break;
                if (next.Indent > indent || IsDash(next.Start, next.End)) throw new Unsupported();
            }
            return hash ? Byml.HashMap(hashes, tag == "!h64") : Byml.Map(map);
        }

        // Does a "key:" start at i? The colon must end the line or be followed by a space.
        private bool IsEntry(int i, int end)
        {
            char c = _text[i];
            if (c is '[' or '{' or '*' or '&' or '!' or '|' or '>' or '%' or '@' or '`' or '?' or '#' or ',' or ']' or '}') return false;

            if (c is '"' or '\'')
            {
                int close = Quoted(i, end);
                if (close < 0) return false;
                close = SkipSpaces(close, end);
                return close < end && _text[close] == ':' && (close + 1 == end || _text[close + 1] == ' ');
            }

            for (int j = i; j < end; j++)
            {
                char d = _text[j];
                if (d == ':' && (j + 1 == end || _text[j + 1] == ' ')) return true;
                if (d == ' ' && j + 1 < end && _text[j + 1] == '#') return false;
            }
            return false;
        }

        // The index just past the quoted scalar that opens at i, or -1 if it doesn't close on this line.
        private int Quoted(int i, int end)
        {
            char quote = _text[i];
            for (int j = i + 1; j < end; j++)
            {
                if (_text[j] == '\\' && quote == '"') j++;
                else if (_text[j] == quote)
                {
                    if (quote == '\'' && j + 1 < end && _text[j + 1] == '\'') j++;
                    else return j + 1;
                }
            }
            return -1;
        }

        // Reads a mapping key and leaves i on its colon.
        private string Key(ref int i, int end)
        {
            string key;
            if (_text[i] is '"' or '\'')
            {
                key = QuotedText(ref i, end);
                i = SkipSpaces(i, end);
            }
            else
            {
                int j = i;
                while (j < end && !(_text[j] == ':' && (j + 1 == end || _text[j + 1] == ' '))) j++;
                if (j >= end) throw new Unsupported();
                int stop = j;
                while (stop > i && _text[stop - 1] == ' ') stop--;
                key = Intern(_text.AsSpan(i, stop - i));
                i = j;
            }

            if (i >= end || _text[i] != ':' || (i + 1 < end && _text[i + 1] != ' ')) throw new Unsupported();
            return key;
        }

        private Byml Inline(ref int i, int end, string? tag, bool anchored)
        {
            char c = _text[i];
            switch (c)
            {
                case '*':
                    if (tag is not null || anchored) throw new Unsupported();
                    return Alias(ref i, end);

                case '[' or '{':
                    return Flow(ref i, end, tag);

                case '"' or '\'':
                    return Typed(tag, quoted: true, QuotedText(ref i, end));

                case '|' or '>' or '%' or '@' or '`':
                    throw new Unsupported();
            }

            int stop = PlainEnd(i, end, flow: false);
            ReadOnlySpan<char> plain = _text.AsSpan(i, stop - i);
            i = stop;
            return tag is null ? Resolve(plain) : Tagged(tag, plain.ToString());
        }

        private Byml Alias(ref int i, int end)
        {
            int stop = i + 1;
            while (stop < end && _text[stop] is not (' ' or ',' or ']' or '}')) stop++;
            string name = _text.Substring(i + 1, stop - i - 1);
            i = stop;
            return _anchors is not null && _anchors.TryGetValue(name, out Byml? shared) ? shared : throw new Unsupported();
        }

        // Where a plain scalar ends: at the end of the line or its trailing comment, or in a flow collection
        // at a delimiter. Trailing spaces are not part of it.
        private int PlainEnd(int i, int end, bool flow)
        {
            int j = i;
            for (; j < end; j++)
            {
                char c = _text[j];
                if (c == ' ' && j + 1 < end && _text[j + 1] == '#') break;
                if (flow && c is ',' or ']' or '}' or '[' or '{') break;
                if (flow && c == ':' && (j + 1 == end || _text[j + 1] is ' ' or ',' or ']' or '}')) break;
            }
            while (j > i && _text[j - 1] == ' ') j--;
            if (j == i) throw new Unsupported();
            return j;
        }
    }
}
