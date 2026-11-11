using System.Collections.Concurrent;
using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace BymlSharp;

internal static partial class BymlYamlWriter
{
    private sealed partial class Writer
    {
        private void Tagged(string tag, string? anchor)
        {
            Properties(anchor, tag);
            Add(' ');
        }

        private void Scalar(Byml node, string? anchor)
        {
            switch (node.Type)
            {
                case BymlType.String: Text(node.String, isKey: false, flow: false); break;
                case BymlType.Null: Add("null"); break;
                case BymlType.Bool: Add(node.Bool ? "true" : "false"); break;

                case BymlType.Int:
                    node.Int.TryFormat(Reserve(11), out int written, default, CultureInfo.InvariantCulture);
                    _length += written;
                    break;

                case BymlType.UInt:
                    Tagged("!u", null);
                    Hex(node.UInt, "X");
                    break;

                case BymlType.Int64:
                    Tagged("!l", anchor);
                    node.Int64.TryFormat(Reserve(20), out written, default, CultureInfo.InvariantCulture);
                    _length += written;
                    break;

                case BymlType.UInt64:
                    Tagged("!ul", anchor);
                    Hex(node.UInt64, "X");
                    break;

                case BymlType.Float when BitConverter.SingleToUInt32Bits(node.Float) is var bits && float.IsNaN(node.Float) && bits != BymlYamlLegacy.QuietNaN:
                    Tagged("!nan", null);
                    Hex(bits, "X8");
                    break;

                case BymlType.Double when BitConverter.DoubleToUInt64Bits(node.Double) is var bits && double.IsNaN(node.Double) && bits != BymlYamlLegacy.QuietNaN64:
                    Tagged("!nan64", anchor);
                    Hex(bits, "X16");
                    break;

                case BymlType.Float:
                    Span<char> text = stackalloc char[32];
                    node.Float.TryFormat(text, out written, "R", CultureInfo.InvariantCulture);
                    Real(text[..written]);
                    break;

                case BymlType.Double:
                    Tagged("!f64", anchor);
                    text = stackalloc char[32];
                    node.Double.TryFormat(text, out written, "R", CultureInfo.InvariantCulture);
                    Real(text[..written]);
                    break;

                case BymlType.Binary:
                    Tagged("!!binary", anchor);
                    Base64(node.Binary);
                    break;

                case BymlType.BinaryAligned:
                    Properties(anchor, "!binary_aligned");
                    Add(" {Alignment: ");
                    Hex((uint)node.BinaryAlignment, "X");
                    Add(", Data: !!binary ");
                    Base64(node.Binary);
                    Add('}');
                    break;

                default:
                    throw new InvalidOperationException($"{node.Type} has no YAML form.");
            }
        }

        private void Hex(ulong value, string format)
        {
            Add("0x");
            value.TryFormat(Reserve(16), out int written, format, CultureInfo.InvariantCulture);
            _length += written;
        }

        private void Base64(byte[] data)
        {
            if (data.Length == 0)
            {
                Add("''");
                return;
            }

            Convert.TryToBase64Chars(data, Reserve((data.Length + 2) / 3 * 4), out int written);
            _length += written;
        }

        private void Real(ReadOnlySpan<char> text)
        {
            if (text is "NaN") Add(".nan");
            else if (text is "Infinity") Add(".inf");
            else if (text is "-Infinity") Add("-.inf");
            else if (text.Contains('.')) Add(text);
            else if (text.IndexOf('E') is var at and >= 0)
            {
                Add(text[..at]);
                Add(".0");
                Add(text[at..]);
            }
            else
            {
                Add(text);
                Add(".0");
            }
        }

        private void Text(string value, bool isKey, bool flow)
        {
            if (isKey && value.Length > SimpleKeyLimit) throw new Unsupported();

            if (value.Length == 0)
            {
                Add("\"\"");
                return;
            }

            if (Plain(value, out bool resolvesToString))
            {
                if (resolvesToString)
                {
                    Add(value);
                }
                else
                {
                    Add('"');
                    Add(value);
                    Add('"');
                }
                return;
            }

            Add(Quote(value, isKey, flow));
        }

        private static bool Plain(string value, out bool resolvesToString)
        {
            resolvesToString = true;

            char first = value[0];
            if (!(char.IsAsciiLetterOrDigit(first) || first == '_')) return false;

            for (int i = 1; i < value.Length; i++)
            {
                char c = value[i];
                if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '/' or '-' || (c == ' ' && i < value.Length - 1))) return false;
            }

            if (char.IsAsciiDigit(first))
            {
                resolvesToString = BymlYamlLegacy.Resolve(value).Type is BymlType.String;
            }
            else if (value.Length <= 5)
            {
                resolvesToString = value is not ("null" or "Null" or "NULL" or "true" or "True" or "TRUE" or "false" or "False" or "FALSE");
            }
            return true;
        }

        private static readonly ConcurrentDictionary<(string Value, bool IsKey, bool Flow), string> s_quoted = new();

        private static string Quote(string value, bool isKey, bool flow)
        {
            if (s_quoted.TryGetValue((value, isKey, flow), out string? known)) return known;

            bool plain = value.Length > 0 && BymlYamlLegacy.Resolve(value) is { Type: BymlType.String };
            Scalar text = new(AnchorName.Empty, TagName.Empty, value, plain ? ScalarStyle.Any : ScalarStyle.DoubleQuoted, plain, !plain);
            Scalar filler = new(AnchorName.Empty, TagName.Empty, isKey ? "v" : "k", ScalarStyle.Plain, true, false);

            StringWriter writer = new();
            Emitter emitter = new(writer, new EmitterSettings().WithBestWidth(int.MaxValue));
            emitter.Emit(new StreamStart());
            emitter.Emit(new DocumentStart());
            emitter.Emit(new MappingStart(AnchorName.Empty, TagName.Empty, true, flow ? MappingStyle.Flow : MappingStyle.Block));
            emitter.Emit(isKey ? text : filler);
            emitter.Emit(isKey ? filler : text);
            emitter.Emit(new MappingEnd());
            emitter.Emit(new DocumentEnd(true));
            emitter.Emit(new StreamEnd());

            string rendered = writer.ToString().TrimEnd('\r', '\n');
            if (flow) rendered = rendered[1..^1];
            string quoted = isKey
                ? rendered.EndsWith(": v", StringComparison.Ordinal) ? rendered[..^3] : throw new Unsupported()
                : rendered.StartsWith("k: ", StringComparison.Ordinal) ? rendered[3..] : throw new Unsupported();

            if (quoted.Contains('\n') || quoted.Contains('\r') || quoted.StartsWith("? ", StringComparison.Ordinal))
                throw new Unsupported();

            if (s_quoted.Count < 8192) s_quoted[(value, isKey, flow)] = quoted;
            return quoted;
        }
    }
}
