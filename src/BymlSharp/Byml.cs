using System.Diagnostics.CodeAnalysis;

namespace BymlSharp;

public sealed class Byml
{
    private readonly object? _ref;
    private readonly long _bits;

    internal int VisitStamp;

    private Byml(BymlType type, object? value, long bits = 0)
    {
        Type = type;
        _ref = value;
        _bits = bits;
    }

    public BymlType Type { get; }

    public static Byml Null { get; } = new(BymlType.Null, null);

    public static IEqualityComparer<Byml> ValueComparer => BymlWriter.ContentComparer.Instance;

    internal static Byml Of(BymlType type, object? value) => new(type, value);
    internal static Byml OfString(string value, int tableIndex) => new(BymlType.String, value, tableIndex);
    internal int StringIndexHint => (int)_bits;

    internal static Byml OfInt(int value) => new(BymlType.Int, null, value);
    internal static Byml OfUInt(uint value) => new(BymlType.UInt, null, value);
    internal static Byml OfFloat(float value) => new(BymlType.Float, null, BitConverter.SingleToInt32Bits(value));
    internal static Byml OfBool(bool value) => new(BymlType.Bool, null, value ? 1 : 0);
    internal static Byml OfInt64(long value) => new(BymlType.Int64, null, value);
    internal static Byml OfUInt64(ulong value) => new(BymlType.UInt64, null, unchecked((long)value));
    internal static Byml OfDouble(double value) => new(BymlType.Double, null, BitConverter.DoubleToInt64Bits(value));
    internal static Byml OfAligned(byte[] value, int alignment) => new(BymlType.BinaryAligned, value, alignment);

    internal object? Value => Type switch
    {
        BymlType.Int => Int,
        BymlType.UInt => UInt,
        BymlType.Float => Float,
        BymlType.Bool => Bool,
        BymlType.Int64 => Int64,
        BymlType.UInt64 => UInt64,
        BymlType.Double => Double,
        _ => _ref,
    };

    // ---- reading ----------------------------------------------------------
    public static Byml FromBinary(ReadOnlySpan<byte> data) => BymlFile.FromBinary(data).Root;

    public static Byml FromFile(string path) => FromBinary(File.ReadAllBytes(path));

    // ---- writing ----------------------------------------------------------
    public byte[] ToBinary(int version = BymlVersion.Default, bool bigEndian = false)
        => new BymlFile(this, version, bigEndian).Write();

    public void ToFile(string path, int version = BymlVersion.Default, bool bigEndian = false)
        => File.WriteAllBytes(path, ToBinary(version, bigEndian));

    // ---- yaml -------------------------------------------------------------
    public static Byml FromYaml(string yaml) => BymlYaml.Read(yaml);

    public static Byml FromYamlFile(string path) => FromYaml(File.ReadAllText(path));

    public string ToYaml(bool anchors = false) => BymlYaml.Write(this, anchors);

    public void ToYamlFile(string path, bool anchors = false) => File.WriteAllText(path, ToYaml(anchors));

    // ---- building ---------------------------------------------------------
    public static Byml Map(IDictionary<string, Byml>? entries = null)
        => new(BymlType.Map, entries as Dictionary<string, Byml>
            ?? new Dictionary<string, Byml>(entries ?? new Dictionary<string, Byml>(), StringComparer.Ordinal));

    public static Byml Array(IEnumerable<Byml>? entries = null)
        => new(BymlType.Array, entries as List<Byml> ?? [.. entries ?? []]);

    public static Byml HashMap(IDictionary<ulong, Byml>? entries = null, bool wide = true)
        => new(wide ? BymlType.HashMap64 : BymlType.HashMap32,
               entries as Dictionary<ulong, Byml> ?? new Dictionary<ulong, Byml>(entries ?? new Dictionary<ulong, Byml>()));

    public static Byml From(string value) => new(BymlType.String, value);
    public static Byml From(bool value) => OfBool(value);
    public static Byml From(int value) => OfInt(value);
    public static Byml From(uint value) => OfUInt(value);
    public static Byml From(long value) => OfInt64(value);
    public static Byml From(ulong value) => OfUInt64(value);
    public static Byml From(float value) => OfFloat(value);
    public static Byml From(double value) => OfDouble(value);
    public static Byml From(byte[] value) => new(BymlType.Binary, value);

    public static Byml From(byte[] value, int alignment) => OfAligned(value, alignment);

    public static implicit operator Byml(string value) => From(value);
    public static implicit operator Byml(bool value) => From(value);
    public static implicit operator Byml(int value) => From(value);
    public static implicit operator Byml(uint value) => From(value);
    public static implicit operator Byml(long value) => From(value);
    public static implicit operator Byml(ulong value) => From(value);
    public static implicit operator Byml(float value) => From(value);
    public static implicit operator Byml(double value) => From(value);

    // ---- containers -------------------------------------------------------
    public bool IsMap => Type is BymlType.Map;
    public bool IsArray => Type is BymlType.Array;
    public bool IsHashMap => Type is BymlType.HashMap32 or BymlType.HashMap64;
    public bool IsContainer => IsMap || IsArray || IsHashMap;

    public Dictionary<string, Byml> AsMap => As<Dictionary<string, Byml>>(BymlType.Map);
    public List<Byml> AsArray => As<List<Byml>>(BymlType.Array);
    public Dictionary<ulong, Byml> AsHashMap => _ref as Dictionary<ulong, Byml> ?? throw Wrong(BymlType.HashMap64);

    public IDictionary<string, Byml> AsMap => As<Dictionary<string, Byml>>(BymlType.Map);

    public IList<Byml> AsArray => As<List<Byml>>(BymlType.Array);

    public IDictionary<ulong, Byml> AsHashMap
        => _ref as Dictionary<ulong, Byml> ?? throw Wrong(BymlType.HashMap64);

    public int Count => _ref switch
    {
        Dictionary<string, Byml> map => map.Count,
        List<Byml> array => array.Count,
        Dictionary<ulong, Byml> hash => hash.Count,
        _ => 0,
    };

    public Byml? this[string key]
    {
        get => _ref is Dictionary<string, Byml> map && map.TryGetValue(key, out Byml? node) ? node : null;
        set => AsMap[key] = value ?? Null;
    }

    public Byml? this[int index]
    {
        get => _ref is List<Byml> array && index >= 0 && index < array.Count ? array[index] : null;
        set => AsArray[index] = value ?? Null;
    }

    public bool TryGet(string key, [NotNullWhen(true)] out Byml? node)
    {
        node = this[key];
        return node is not null;
    }

    // ---- values -----------------------------------------------------------

    public bool IsNull => Type is BymlType.Null;

    public string String => As<string>(BymlType.String);
    public bool Bool => Type is BymlType.Bool ? _bits != 0 : throw Wrong(BymlType.Bool);
    public int Int => Type is BymlType.Int ? (int)_bits : throw Wrong(BymlType.Int);
    public uint UInt => Type is BymlType.UInt ? (uint)_bits : throw Wrong(BymlType.UInt);
    public long Int64 => Type is BymlType.Int64 ? _bits : throw Wrong(BymlType.Int64);
    public ulong UInt64 => Type is BymlType.UInt64 ? unchecked((ulong)_bits) : throw Wrong(BymlType.UInt64);
    public float Float => Type is BymlType.Float ? BitConverter.Int32BitsToSingle((int)_bits) : throw Wrong(BymlType.Float);
    public double Double => Type is BymlType.Double ? BitConverter.Int64BitsToDouble(_bits) : throw Wrong(BymlType.Double);

    public byte[] Binary => Type is BymlType.Binary or BymlType.BinaryAligned ? (byte[])_ref! : throw Wrong(BymlType.Binary);

    public int BinaryAlignment => Type is BymlType.BinaryAligned ? (int)_bits : 0;

    public double? AsNumber() => Type switch
    {
        BymlType.Int => (int)_bits,
        BymlType.UInt => (uint)_bits,
        BymlType.Int64 => _bits,
        BymlType.UInt64 => unchecked((ulong)_bits),
        BymlType.Float => Float,
        BymlType.Double => Double,
        BymlType.Bool => _bits != 0 ? 1 : 0,
        _ => null,
    };

    public string? AsString() => _ref as string;

    public override string ToString() => Type switch
    {
        BymlType.Map => $"Map({Count})",
        BymlType.Array => $"Array({Count})",
        BymlType.HashMap32 or BymlType.HashMap64 => $"HashMap({Count})",
        BymlType.Null => "null",
        BymlType.Binary or BymlType.BinaryAligned => $"Binary({Binary.Length})",
        _ => Value?.ToString() ?? "null",
    };

    private T As<T>(BymlType expected)
        => _ref is T value ? value : throw Wrong(expected);

    private InvalidOperationException Wrong(BymlType expected)
        => new($"Node is {Type}, not {expected}.");
}
