namespace BymlSharp;

internal static partial class BymlYamlReader
{
    private sealed partial class Parser
    {
        private Byml Flow(ref int i, int end, string? tag)
        {
            bool sequence = _text[i] == '[';
            char close = sequence ? ']' : '}';
            i++;

            if (sequence)
            {
                if (tag is not null) throw new Unsupported();

                List<Byml> items = [];
                i = SkipSpaces(i, end);
                while (true)
                {
                    if (i >= end) throw new Unsupported();
                    if (_text[i] == ']') break;

                    items.Add(FlowValue(ref i, end));
                    i = SkipSpaces(i, end);
                    if (i >= end) throw new Unsupported();
                    if (_text[i] == ',') i = SkipSpaces(i + 1, end);
                    else if (_text[i] != ']') throw new Unsupported();
                }
                i++;
                return Byml.Array(items);
            }

            if (tag == "!binary_aligned") return FlowAligned(ref i, end);

            bool hash = tag is "!h32" or "!h64";
            if (tag is not null && !hash) throw new Unsupported();

            Dictionary<string, Byml>? map = hash ? null : new(8, StringComparer.Ordinal);
            Dictionary<ulong, Byml>? hashes = hash ? [] : null;

            i = SkipSpaces(i, end);
            while (true)
            {
                if (i >= end) throw new Unsupported();
                if (_text[i] == close) break;

                string key = FlowKey(ref i, end);
                i = SkipSpaces(i + 1, end);
                Byml value = FlowValue(ref i, end);
                if (hash) hashes![BymlYamlLegacy.Integer(key) ?? throw new Unsupported()] = value;
                else map![key] = value;

                i = SkipSpaces(i, end);
                if (i >= end) throw new Unsupported();
                if (_text[i] == ',') i = SkipSpaces(i + 1, end);
                else if (_text[i] != close) throw new Unsupported();
            }
            i++;
            return hash ? Byml.HashMap(hashes, tag == "!h64") : Byml.Map(map);
        }

        private Byml FlowAligned(ref int i, int end)
        {
            byte[]? data = null;
            int alignment = 0;

            i = SkipSpaces(i, end);
            while (true)
            {
                if (i >= end) throw new Unsupported();
                if (_text[i] == '}') break;

                string key = FlowKey(ref i, end);
                i = SkipSpaces(i + 1, end);
                while (i < end && _text[i] == '!')
                {
                    while (i < end && _text[i] != ' ') i++;
                    i = SkipSpaces(i, end);
                }
                if (i >= end) throw new Unsupported();

                string raw;
                if (_text[i] is '"' or '\'') raw = QuotedText(ref i, end);
                else
                {
                    int from = i;
                    i = PlainEnd(i, end, flow: true);
                    raw = _text.Substring(from, i - from);
                }
                if (key == "Data") data = Convert.FromBase64String(BymlYamlLegacy.Strip(raw));
                else if (key == "Alignment") alignment = checked((int)(BymlYamlLegacy.Integer(raw) ?? throw new Unsupported()));
                else throw new Unsupported();

                i = SkipSpaces(i, end);
                if (i >= end) throw new Unsupported();
                if (_text[i] == ',') i = SkipSpaces(i + 1, end);
                else if (_text[i] != '}') throw new Unsupported();
            }
            i++;
            return Byml.From(data ?? [], alignment);
        }

        private string FlowKey(ref int i, int end)
        {
            if (_text[i] is '"' or '\'')
            {
                string quoted = QuotedText(ref i, end);
                i = SkipSpaces(i, end);
                if (i >= end || _text[i] != ':') throw new Unsupported();
                return quoted;
            }

            int j = i;
            while (j < end && !(_text[j] == ':' && (j + 1 == end || _text[j + 1] is ' ' or ',' or '}' or ']')))
            {
                if (_text[j] is ',' or '[' or ']' or '{' or '}') throw new Unsupported();
                j++;
            }
            if (j >= end) throw new Unsupported();

            int stop = j;
            while (stop > i && _text[stop - 1] == ' ') stop--;
            if (stop == i) throw new Unsupported();

            string key = Intern(_text.AsSpan(i, stop - i));
            i = j;
            return key;
        }

        private Byml FlowValue(ref int i, int end)
        {
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
            if (i >= end) throw new Unsupported();

            Byml node;
            switch (_text[i])
            {
                case '*':
                    if (tag is not null || anchor is not null) throw new Unsupported();
                    return Alias(ref i, end);

                case '[' or '{':
                    node = Flow(ref i, end, tag);
                    break;

                case '"' or '\'':
                    node = Typed(tag, quoted: true, QuotedText(ref i, end));
                    break;

                case '|' or '>' or '%' or '@' or '`':
                    throw new Unsupported();

                default:
                    int stop = PlainEnd(i, end, flow: true);
                    ReadOnlySpan<char> plain = _text.AsSpan(i, stop - i);
                    i = stop;
                    node = tag is null ? Resolve(plain) : Tagged(tag, plain.ToString());
                    break;
            }

            if (anchor is not null) (_anchors ??= new(StringComparer.Ordinal))[anchor] = node;
            return node;
        }
    }
}
