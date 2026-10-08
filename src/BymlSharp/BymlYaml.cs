using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace BymlSharp;

// Tags follow oead: plain ints are Int, plain floats Float, !u UInt, !l Int64, !ul UInt64,
// !f64 Double, !!binary Binary. Additions: !binary_aligned {Alignment, Data}, !h32 / !h64 hash maps.
internal static partial class BymlYaml
{
    private const string BinaryTag = "tag:yaml.org,2002:binary";
    private const int FlowLimit = 8;

    public static string Write(Byml root)
    {
        StringWriter text = new();
        Emitter emitter = new(text, new EmitterSettings().WithBestWidth(int.MaxValue));
        emitter.Emit(new StreamStart());
        emitter.Emit(new DocumentStart());
        Emit(emitter, root);
        emitter.Emit(new DocumentEnd(true));
        emitter.Emit(new StreamEnd());
        return text.ToString();
    }

    public static Byml Read(string yaml)
    {
        Parser parser = new(new StringReader(yaml));
        parser.Consume<StreamStart>();
        if (parser.TryConsume<StreamEnd>(out _)) return Byml.Null;
        parser.Consume<DocumentStart>();
        Byml root = Parse(parser);
        parser.Consume<DocumentEnd>();
        return root;
    }

    // ---- writing ----------------------------------------------------------

    private static void Emit(IEmitter e, Byml node)
    {
        switch (node.Type)
        {
            case BymlType.Map:
                e.Emit(new MappingStart(AnchorName.Empty, TagName.Empty, true, Flow(node) ? MappingStyle.Flow : MappingStyle.Block));
                foreach (var (key, value) in node.AsMap)
                {
                    Text(e, key);
                    Emit(e, value);
                }
                e.Emit(new MappingEnd());
                break;

            case BymlType.Array:
                e.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, true, Flow(node) ? SequenceStyle.Flow : SequenceStyle.Block));
                foreach (Byml value in node.AsArray) Emit(e, value);
                e.Emit(new SequenceEnd());
                break;

            case BymlType.HashMap32 or BymlType.HashMap64:
                string tag = node.Type is BymlType.HashMap32 ? "!h32" : "!h64";
                e.Emit(new MappingStart(AnchorName.Empty, new TagName(tag), false, Flow(node) ? MappingStyle.Flow : MappingStyle.Block));
                foreach (var (key, value) in node.AsHashMap)
                {
                    Plain(e, $"0x{key:X}");
                    Emit(e, value);
                }
                e.Emit(new MappingEnd());
                break;

            case BymlType.String: Text(e, node.String); break;
            case BymlType.Null: Plain(e, "null"); break;
            case BymlType.Bool: Plain(e, node.Bool ? "true" : "false"); break;
            case BymlType.Int: Plain(e, node.Int.ToString(CultureInfo.InvariantCulture)); break;
            case BymlType.UInt: Tagged(e, "!u", $"0x{node.UInt:X}"); break;
            case BymlType.Int64: Tagged(e, "!l", node.Int64.ToString(CultureInfo.InvariantCulture)); break;
            case BymlType.UInt64: Tagged(e, "!ul", $"0x{node.UInt64:X}"); break;
            case BymlType.Float: Plain(e, FormatReal(node.Float.ToString("R", CultureInfo.InvariantCulture))); break;
            case BymlType.Double: Tagged(e, "!f64", FormatReal(node.Double.ToString("R", CultureInfo.InvariantCulture))); break;
            case BymlType.Binary: Tagged(e, BinaryTag, Convert.ToBase64String(node.Binary)); break;

            case BymlType.BinaryAligned:
                e.Emit(new MappingStart(AnchorName.Empty, new TagName("!binary_aligned"), false, MappingStyle.Flow));
                Plain(e, "Alignment");
                Plain(e, $"0x{node.BinaryAlignment:X}");
                Plain(e, "Data");
                Tagged(e, BinaryTag, Convert.ToBase64String(node.Binary));
                e.Emit(new MappingEnd());
                break;

            default: throw new InvalidOperationException($"{node.Type} has no YAML form.");
        }
    }

    private static bool Flow(Byml node) => node.Count <= FlowLimit && (node.Type switch
    {
        BymlType.Map => node.AsMap.Values,
        BymlType.Array => node.AsArray,
        _ => node.AsHashMap.Values,
    }).All(v => !v.IsContainer && v.Type is not (BymlType.Binary or BymlType.BinaryAligned or BymlType.String));

    private static void Plain(IEmitter e, string value)
        => e.Emit(new Scalar(AnchorName.Empty, TagName.Empty, value, ScalarStyle.Plain, true, false));

    private static void Tagged(IEmitter e, string tag, string value)
        => e.Emit(new Scalar(AnchorName.Empty, new TagName(tag), value, ScalarStyle.Plain, false, false));

    private static void Text(IEmitter e, string value)
    {
        bool plain = value.Length > 0 && Resolve(value) is { Type: BymlType.String };
        e.Emit(new Scalar(AnchorName.Empty, TagName.Empty, value,
            plain ? ScalarStyle.Any : ScalarStyle.DoubleQuoted, plain, !plain));
    }

    private static string FormatReal(string text) => text switch
    {
        "NaN" => ".nan",
        "Infinity" => ".inf",
        "-Infinity" => "-.inf",
        _ when text.Contains('.') => text,
        _ when text.IndexOf('E') is var at and >= 0 => text.Insert(at, ".0"),
        _ => text + ".0",
    };

    // ---- reading ----------------------------------------------------------

    private static Byml Parse(IParser p)
    {
        if (p.TryConsume<Scalar>(out Scalar? scalar)) return FromScalar(scalar);

        if (p.TryConsume<SequenceStart>(out _))
        {
            List<Byml> array = [];
            while (!p.TryConsume<SequenceEnd>(out _)) array.Add(Parse(p));
            return Byml.Array(array);
        }

        if (p.TryConsume<MappingStart>(out MappingStart? map))
        {
            string tag = map.Tag.IsEmpty ? "" : map.Tag.Value;
            switch (tag)
            {
                case "":
                    Dictionary<string, Byml> entries = new(StringComparer.Ordinal);
                    while (!p.TryConsume<MappingEnd>(out _))
                        entries[p.Consume<Scalar>().Value] = Parse(p);
                    return Byml.Map(entries);

                case "!h32" or "!h64":
                    Dictionary<ulong, Byml> hashes = [];
                    while (!p.TryConsume<MappingEnd>(out _))
                    {
                        Scalar key = p.Consume<Scalar>();
                        hashes[Integer(key.Value) ?? throw Error(key, "hash map keys must be integers")] = Parse(p);
                    }
                    return Byml.HashMap(hashes, tag == "!h64");

                case "!binary_aligned":
                    byte[]? data = null;
                    int alignment = 0;
                    while (!p.TryConsume<MappingEnd>(out _))
                    {
                        Scalar key = p.Consume<Scalar>();
                        Scalar value = p.Consume<Scalar>();
                        if (key.Value == "Data") data = Convert.FromBase64String(Strip(value.Value));
                        else if (key.Value == "Alignment") alignment = checked((int)(Integer(value.Value) ?? throw Error(value, "alignment must be an integer")));
                        else throw Error(key, $"unexpected key {key.Value}");
                    }
                    return Byml.From(data ?? [], alignment);

                default: throw Error(map, $"unknown mapping tag {tag}");
            }
        }

        throw new YamlException(p.Current!.Start, p.Current.End, $"Unexpected {p.Current.GetType().Name}.");
    }

    private static Byml FromScalar(Scalar s)
    {
        if (s.Tag.IsEmpty)
            return s.Style is ScalarStyle.Plain ? Resolve(s.Value) : Byml.From(s.Value);

        string v = s.Value;
        return s.Tag.Value switch
        {
            "!u" => Byml.From(checked((uint)(Integer(v) ?? throw Error(s, "expected an integer")))),
            "!l" => Byml.From(Signed(v) ?? throw Error(s, "expected an integer")),
            "!ul" => Byml.From(Integer(v) ?? throw Error(s, "expected an integer")),
            "!f64" => Byml.From(Real(v) ?? throw Error(s, "expected a number")),
            BinaryTag => Byml.From(Convert.FromBase64String(Strip(v))),
            "tag:yaml.org,2002:str" or "!" => Byml.From(v),
            "tag:yaml.org,2002:null" => Byml.Null,
            "tag:yaml.org,2002:bool" => Resolve(v) is { Type: BymlType.Bool } b ? b : throw Error(s, "expected a bool"),
            "tag:yaml.org,2002:int" => Resolve(v) is { Type: BymlType.Int or BymlType.Int64 or BymlType.UInt64 } i ? i : throw Error(s, "expected an integer"),
            "tag:yaml.org,2002:float" => Byml.From(Single(v) ?? throw Error(s, "expected a number")),
            var tag => throw Error(s, $"unknown tag {tag}"),
        };
    }

    // An untagged plain scalar, typed by the YAML core schema.
    private static Byml Resolve(string v)
    {
        if (v is "" or "~" or "null" or "Null" or "NULL") return Byml.Null;
        if (v is "true" or "True" or "TRUE") return Byml.From(true);
        if (v is "false" or "False" or "FALSE") return Byml.From(false);

        if (Signed(v) is long l) return l is >= int.MinValue and <= int.MaxValue ? Byml.From((int)l) : Byml.From(l);
        if (Integer(v) is ulong u) return Byml.From(u);
        if (FloatPattern().IsMatch(v) && Single(v) is float f) return Byml.From(f);
        return Byml.From(v);
    }

    private static long? Signed(string v)
    {
        bool negative = v.StartsWith('-');
        if (negative || v.StartsWith('+')) v = v[1..];
        if (v.StartsWith('-') || v.StartsWith('+')) return null;
        if (Integer(v) is not ulong magnitude) return null;
        if (negative) return magnitude <= (ulong)long.MaxValue + 1 ? unchecked(-(long)magnitude) : null;
        return magnitude <= long.MaxValue ? (long)magnitude : null;
    }

    private static ulong? Integer(string v)
    {
        if (v.StartsWith('+')) v = v[1..];
        if (v.StartsWith("0x") && ulong.TryParse(v.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong hex)) return hex;
        if (v.StartsWith("0o"))
        {
            try { return v.Length > 2 ? Convert.ToUInt64(v[2..], 8) : null; }
            catch (Exception ex) when (ex is FormatException or OverflowException) { return null; }
        }
        return v.Length > 0 && char.IsAsciiDigit(v[0]) && ulong.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out ulong dec) ? dec : null;
    }

    private static double? Real(string v) => v switch
    {
        ".nan" or ".NaN" or ".NAN" => double.NaN,
        ".inf" or ".Inf" or ".INF" or "+.inf" or "+.Inf" or "+.INF" => double.PositiveInfinity,
        "-.inf" or "-.Inf" or "-.INF" => double.NegativeInfinity,
        _ => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : null,
    };

    private static float? Single(string v) => Real(v) is double d
        ? double.IsFinite(d) ? float.Parse(v, NumberStyles.Float, CultureInfo.InvariantCulture) : (float)d
        : null;

    private static string Strip(string base64) => string.Concat(base64.Where(c => !char.IsWhiteSpace(c)));

    private static YamlException Error(ParsingEvent at, string message) => new(at.Start, at.End, message);

    [GeneratedRegex(@"^([-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?|[-+]?\.(inf|Inf|INF)|\.(nan|NaN|NAN))$")]
    private static partial Regex FloatPattern();
}
