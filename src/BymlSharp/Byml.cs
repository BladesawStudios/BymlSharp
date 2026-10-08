using System.Diagnostics.CodeAnalysis;

namespace BymlSharp;

public sealed class Byml
{
    private readonly object? _value;

    private Byml(BymlType type, object? value)
    {
        Type = type;
        _value = value;
    }

    public BymlType Type { get; }

    public static Byml Null { get; } = new(BymlType.Null, null);

    internal static Byml Of(BymlType type, object? value) => new(type, value);

    internal object? Value => _value;

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

    /// <summary>
    /// Plain YAML that oead and other tools read, with every node written where it appears, so editing one copy of a
    /// node that the binary shares leaves the others alone. With <paramref name="anchors"/>, a node the document
    /// shares becomes a YAML anchor and aliases, which keeps the text self-contained but couples the copies: editing
    /// the anchored one changes them all, and tools that don't resolve aliases read them as the string "*1".
    /// </summary>
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
    public static Byml From(bool value) => new(BymlType.Bool, value);
    public static Byml From(int value) => new(BymlType.Int, value);
    public static Byml From(uint value) => new(BymlType.UInt, value);
    public static Byml From(long value) => new(BymlType.Int64, value);
    public static Byml From(ulong value) => new(BymlType.UInt64, value);
    public static Byml From(float value) => new(BymlType.Float, value);
    public static Byml From(double value) => new(BymlType.Double, value);
    public static Byml From(byte[] value) => new(BymlType.Binary, value);

    public static Byml From(byte[] value, int alignment) => new(BymlType.BinaryAligned, (value, alignment));

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

    public IDictionary<string, Byml> AsMap => As<Dictionary<string, Byml>>(BymlType.Map);

    public IList<Byml> AsArray => As<List<Byml>>(BymlType.Array);

    public IDictionary<ulong, Byml> AsHashMap
        => _value as Dictionary<ulong, Byml> ?? throw Wrong(BymlType.HashMap64);

    public int Count => _value switch
    {
        Dictionary<string, Byml> map => map.Count,
        List<Byml> array => array.Count,
        Dictionary<ulong, Byml> hash => hash.Count,
        _ => 0,
    };

    public Byml? this[string key]
    {
        get => _value is Dictionary<string, Byml> map && map.TryGetValue(key, out Byml? node) ? node : null;
        set => AsMap[key] = value ?? Null;
    }

    public Byml? this[int index]
    {
        get => _value is List<Byml> array && index >= 0 && index < array.Count ? array[index] : null;
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
    public bool Bool => As<bool>(BymlType.Bool);
    public int Int => As<int>(BymlType.Int);
    public uint UInt => As<uint>(BymlType.UInt);
    public long Int64 => As<long>(BymlType.Int64);
    public ulong UInt64 => As<ulong>(BymlType.UInt64);
    public float Float => As<float>(BymlType.Float);
    public double Double => As<double>(BymlType.Double);

    public byte[] Binary => _value switch
    {
        byte[] data => data,
        (byte[] data, int _) => data,
        _ => throw Wrong(BymlType.Binary),
    };

    public int BinaryAlignment => _value is (byte[] _, int alignment) ? alignment : 0;

    public double? AsNumber() => _value switch
    {
        int v => v,
        uint v => v,
        long v => v,
        ulong v => v,
        float v => v,
        double v => v,
        bool v => v ? 1 : 0,
        _ => null,
    };

    public string? AsString() => _value as string;

    public override string ToString() => Type switch
    {
        BymlType.Map => $"Map({Count})",
        BymlType.Array => $"Array({Count})",
        BymlType.HashMap32 or BymlType.HashMap64 => $"HashMap({Count})",
        BymlType.Null => "null",
        BymlType.Binary or BymlType.BinaryAligned => $"Binary({Binary.Length})",
        _ => _value?.ToString() ?? "null",
    };

    private T As<T>(BymlType expected)
        => _value is T value ? value : throw Wrong(expected);

    private InvalidOperationException Wrong(BymlType expected)
        => new($"Node is {Type}, not {expected}.");
}
