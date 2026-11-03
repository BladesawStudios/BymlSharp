using System.Globalization;
using System.Text;

namespace BymlSharp;

internal static partial class BymlYamlReader
{
    private sealed partial class Parser
    {
        private string QuotedText(ref int i, int end)
        {
            char quote = _text[i];
            int close = Quoted(i, end);
            if (close < 0) throw new Unsupported();

            ReadOnlySpan<char> body = _text.AsSpan(i + 1, close - i - 2);
            i = close;

            if (quote == '\'')
                return body.Contains('\'') ? body.ToString().Replace("''", "'", StringComparison.Ordinal) : Intern(body);
            return body.Contains('\\') ? Unescape(body) : Intern(body);
        }

        private static string Unescape(ReadOnlySpan<char> body)
        {
            System.Text.StringBuilder text = new(body.Length);
            for (int j = 0; j < body.Length; j++)
            {
                char c = body[j];
                if (c != '\\')
                {
                    text.Append(c);
                    continue;
                }

                if (++j == body.Length) throw new Unsupported();
                switch (body[j])
                {
                    case '0': text.Append('\0'); break;
                    case 'a': text.Append('\a'); break;
                    case 'b': text.Append('\b'); break;
                    case 't' or '\t': text.Append('\t'); break;
                    case 'n': text.Append('\n'); break;
                    case 'v': text.Append('\v'); break;
                    case 'f': text.Append('\f'); break;
                    case 'r': text.Append('\r'); break;
                    case 'e': text.Append('\u001B'); break;
                    case ' ': text.Append(' '); break;
                    case '"': text.Append('"'); break;
                    case '/': text.Append('/'); break;
                    case '\\': text.Append('\\'); break;
                    case 'x' or 'u' or 'U':
                        int digits = body[j] == 'x' ? 2 : body[j] == 'u' ? 4 : 8;
                        if (j + digits >= body.Length) throw new Unsupported();
                        if (!int.TryParse(body.Slice(j + 1, digits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int code)) throw new Unsupported();
                        if (digits == 8)
                        {
                            if (code is < 0 or > 0x10FFFF || code is >= 0xD800 and <= 0xDFFF) throw new Unsupported();
                            text.Append(char.ConvertFromUtf32(code));
                        }
                        else
                        {
                            text.Append((char)code);
                        }
                        j += digits;
                        break;
                    default: throw new Unsupported();
                }
            }
            return text.ToString();
        }

        private string Intern(ReadOnlySpan<char> span)
        {
            if (span.Length == 0) return "";
            if (span.Length > 64) return span.ToString();
            if (_lookup.TryGetValue(span, out string? known)) return known;

            string created = span.ToString();
            _names[created] = created;
            return created;
        }

        private static string Tag(ReadOnlySpan<char> token)
        {
            if (token.Length >= 2 && token[1] == '<') throw new Unsupported();
            if (token.Length > 2 && token[1] == '!') return "tag:yaml.org,2002:" + token[2..].ToString();
            return token.ToString();
        }

        private Byml Typed(string? tag, bool quoted, string value)
        {
            if (tag is null) return quoted ? Byml.From(value) : BymlYamlLegacy.Resolve(value);
            return Tagged(tag, value);
        }

        private static Byml Tagged(string tag, string v) => tag switch
        {
            "!u" => Byml.From(checked((uint)(BymlYamlLegacy.Integer(v) ?? throw new Unsupported()))),
            "!l" or "!sl" => Byml.From(BymlYamlLegacy.Signed(v) ?? throw new Unsupported()),
            "!ul" => Byml.From(BymlYamlLegacy.Integer(v) ?? throw new Unsupported()),
            "!f64" => Byml.From(BymlYamlLegacy.Real(v) ?? throw new Unsupported()),
            "!nan" => Byml.From(BitConverter.UInt32BitsToSingle(checked((uint)(BymlYamlLegacy.Integer(v) ?? throw new Unsupported())))),
            "!nan64" => Byml.From(BitConverter.UInt64BitsToDouble(BymlYamlLegacy.Integer(v) ?? throw new Unsupported())),
            BymlYamlLegacy.BinaryTag => Byml.From(Convert.FromBase64String(BymlYamlLegacy.Strip(v))),
            "tag:yaml.org,2002:str" or "!" => Byml.From(v),
            _ => throw new Unsupported(),
        };

        // An untagged plain scalar, typed by the YAML core schema (see BymlYamlLegacy.Resolve, whose answers
        // these shortcuts must match).
        private Byml Resolve(ReadOnlySpan<char> v)
        {
            if (v.Length == 0) return Byml.Null;

            char c = v[0];
            if (char.IsAsciiLetter(c))
            {
                if (v.Length <= 5 && c is 'n' or 'N' or 't' or 'T' or 'f' or 'F')
                {
                    if (v is "null" or "Null" or "NULL") return Byml.Null;
                    if (v is "true" or "True" or "TRUE") return Byml.From(true);
                    if (v is "false" or "False" or "FALSE") return Byml.From(false);
                }
                return Byml.From(Intern(v));
            }

            if (c == '~' && v.Length == 1) return Byml.Null;

            if (char.IsAsciiDigit(c) || c is '-' or '+' or '.')
            {
                if (Number(v) is { } number) return number;
                return BymlYamlLegacy.Resolve(v.ToString());
            }

            return Byml.From(Intern(v));
        }

        // Plain decimal integers and reals; null for everything else (hex, octal, .inf, strings, odd sizes).
        private static Byml? Number(ReadOnlySpan<char> v)
        {
            int i = 0;
            bool negative = v[0] == '-';
            if (negative || v[0] == '+') i++;

            int digitsStart = i;
            while (i < v.Length && char.IsAsciiDigit(v[i])) i++;
            int whole = i - digitsStart;

            if (i == v.Length)
            {
                if (whole is 0 or > 18) return null;

                long magnitude = 0;
                for (int j = digitsStart; j < v.Length; j++) magnitude = magnitude * 10 + (v[j] - '0');
                long value = negative ? -magnitude : magnitude;
                return value is >= int.MinValue and <= int.MaxValue ? Byml.From((int)value) : Byml.From(value);
            }

            int fraction = 0;
            if (v[i] == '.')
            {
                i++;
                int fractionStart = i;
                while (i < v.Length && char.IsAsciiDigit(v[i])) i++;
                fraction = i - fractionStart;
            }
            if (whole == 0 && fraction == 0) return null;

            if (i < v.Length && v[i] is 'e' or 'E')
            {
                i++;
                if (i < v.Length && v[i] is '-' or '+') i++;
                int exponentStart = i;
                while (i < v.Length && char.IsAsciiDigit(v[i])) i++;
                if (i == exponentStart) return null;
            }
            if (i != v.Length) return null;

            return float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float real) && float.IsFinite(real)
                ? Byml.From(real)
                : null;
        }
    }
}
