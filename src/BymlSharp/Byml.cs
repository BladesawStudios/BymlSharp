using System.Diagnostics.CodeAnalysis;

namespace BymlSharp;

/// <summary>
/// One node of a BYML document: a container, a value, or nothing.
/// </summary>
/// <remarks>
/// Read only. A document is parsed into managed nodes once and then walked, which suits the
/// way these files are used - opened, read through, and dropped - and keeps callers free of
/// the spans and offsets the binary form is made of.
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

    /// <summary>Reads a document from its binary form.</summary>
    /// <exception cref="InvalidDataException">The data is not a BYML document this can read.</exception>
    public static Byml FromBinary(ReadOnlySpan<byte> data) => BymlReader.Read(data);

    /// <summary>Reads a document from a file.</summary>
    /// <remarks>
    /// The file must already be decompressed. Most shipped documents are zstd compressed, and
    /// several titles compress them against a dictionary the game carries, which is a concern
    /// for whatever knows where those dictionaries live rather than for this.
    /// </remarks>
    public static Byml FromFile(string path) => FromBinary(File.ReadAllBytes(path));

    // ---- containers -------------------------------------------------------

    public bool IsMap => Type is BymlType.Map;
    public bool IsArray => Type is BymlType.Array;
    public bool IsHashMap => Type is BymlType.HashMap32 or BymlType.HashMap64;

    /// <summary>The entries of a map, by name.</summary>
    public IReadOnlyDictionary<string, Byml> Map => As<IReadOnlyDictionary<string, Byml>>(BymlType.Map);

    /// <summary>The entries of an array, in order.</summary>
    public IReadOnlyList<Byml> Array => As<IReadOnlyList<Byml>>(BymlType.Array);

    /// <summary>The entries of a hash map, by key. 32-bit keys are widened.</summary>
    public IReadOnlyDictionary<ulong, Byml> HashMap
        => Type is BymlType.HashMap32 or BymlType.HashMap64
            ? (IReadOnlyDictionary<ulong, Byml>)_value!
            : throw Wrong(BymlType.HashMap64);

    /// <summary>How many entries a container holds; 0 for anything else.</summary>
    public int Count => _value switch
    {
        IReadOnlyDictionary<string, Byml> map => map.Count,
        IReadOnlyList<Byml> array => array.Count,
        IReadOnlyDictionary<ulong, Byml> hash => hash.Count,
        _ => 0,
    };

    /// <summary>The named entry of a map, or null when it is absent or this is not a map.</summary>
    public Byml? this[string key]
        => _value is IReadOnlyDictionary<string, Byml> map && map.TryGetValue(key, out Byml? node)
            ? node
            : null;

    /// <summary>The entry at an index, or null when it is out of range or this is not an array.</summary>
    public Byml? this[int index]
        => _value is IReadOnlyList<Byml> array && index >= 0 && index < array.Count
            ? array[index]
            : null;

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
    public byte[] Binary => As<byte[]>(BymlType.Binary);

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
        BymlType.Binary or BymlType.BinaryAligned => $"Binary({((byte[]?)_value)?.Length ?? 0})",
        _ => _value?.ToString() ?? "null",
    };

    private T As<T>(BymlType expected)
        => _value is T value ? value : throw Wrong(expected);

    private InvalidOperationException Wrong(BymlType expected)
        => new($"Node is {Type}, not {expected}.");
}
