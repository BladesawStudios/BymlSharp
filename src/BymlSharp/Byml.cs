using System.Diagnostics.CodeAnalysis;

namespace BymlSharp;

/// <summary>
/// One node of a BYML document: a container, a value, or nothing.
/// </summary>
/// <remarks>
/// A document is parsed into managed nodes and can be edited and written back. Containers are
/// handed out as the live collections, so a tree read from a file can be changed in place -
/// which is the usual reason to open one of these at all.
/// </remarks>
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

    /// <summary>Reads the root node of a document from its binary form.</summary>
    /// <exception cref="InvalidDataException">The data is not a BYML document this can read.</exception>
    public static Byml FromBinary(ReadOnlySpan<byte> data) => BymlFile.FromBinary(data).Root;

    /// <summary>Reads the root node of a document from a file.</summary>
    /// <remarks>
    /// The file must already be decompressed. Most shipped documents are zstd compressed, and
    /// several titles compress them against a dictionary the game carries, which is a concern
    /// for whatever knows where those dictionaries live rather than for this.
    /// </remarks>
    public static Byml FromFile(string path) => FromBinary(File.ReadAllBytes(path));

    // ---- writing ----------------------------------------------------------

    /// <summary>Writes this node as a document of the given version and byte order.</summary>
    /// <param name="version">
    /// The format version to write. Versions differ in which node types they allow; see
    /// <see cref="BymlVersion"/>.
    /// </param>
    public byte[] ToBinary(int version = BymlVersion.Default, bool bigEndian = false)
        => new BymlFile(this, version, bigEndian).Write();

    public void ToFile(string path, int version = BymlVersion.Default, bool bigEndian = false)
        => File.WriteAllBytes(path, ToBinary(version, bigEndian));

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

    /// <summary>Binary data the format keeps at a stated alignment.</summary>
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

    /// <summary>The entries of a map, by name. The live collection, so edits reach the tree.</summary>
    public IDictionary<string, Byml> AsMap => As<Dictionary<string, Byml>>(BymlType.Map);

    /// <summary>The entries of an array, in order. The live collection.</summary>
    public IList<Byml> AsArray => As<List<Byml>>(BymlType.Array);

    /// <summary>The entries of a hash map, by key. 32-bit keys are widened.</summary>
    public IDictionary<ulong, Byml> AsHashMap
        => _value as Dictionary<ulong, Byml> ?? throw Wrong(BymlType.HashMap64);

    /// <summary>How many entries a container holds; 0 for anything else.</summary>
    public int Count => _value switch
    {
        Dictionary<string, Byml> map => map.Count,
        List<Byml> array => array.Count,
        Dictionary<ulong, Byml> hash => hash.Count,
        _ => 0,
    };

    /// <summary>The named entry of a map, or null when it is absent or this is not a map.</summary>
    public Byml? this[string key]
    {
        get => _value is Dictionary<string, Byml> map && map.TryGetValue(key, out Byml? node) ? node : null;
        set => AsMap[key] = value ?? Null;
    }

    /// <summary>The entry at an index, or null when it is out of range or this is not an array.</summary>
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

    /// <summary>The alignment a <see cref="BymlType.BinaryAligned"/> node asks for; 0 otherwise.</summary>
    public int BinaryAlignment => _value is (byte[] _, int alignment) ? alignment : 0;

    /// <summary>
    /// The node as a number, whatever width or sign it was stored at.
    /// </summary>
    /// <remarks>
    /// Placement files are not consistent about this - the same field is a float in one actor
    /// and an integer in the next - so reading them by exact type means handling both
    /// everywhere.
    /// </remarks>
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

    /// <summary>The node as text, or null when it is not a string.</summary>
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
