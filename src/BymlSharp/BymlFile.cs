namespace BymlSharp;

/// <summary>
/// Known format versions.
/// </summary>
/// <remarks>
/// The version is a header field, and in practice it says which title wrote the document
/// rather than changing how it is laid out - the containers, the tables and the node encoding
/// are the same across the range. Established readers pass it through without gating node
/// types on it, and this does the same: a document is written back at whatever version it
/// was read at unless the caller asks for another.
///
/// What does vary is which types a title happens to use. The hash-map containers only turn up
/// in version 7 documents, so writing one into an older version will produce a file that
/// nothing expecting that version knows how to read - the format allows it, the reader on the
/// other side may not.
/// </remarks>
public static class BymlVersion
{
    /// <summary>Wii U era documents, usually big endian.</summary>
    public const int WiiU = 2;

    /// <summary>Switch era documents, little endian.</summary>
    public const int Switch = 3;

    /// <summary>The version Tears of the Kingdom writes; the first to use hash maps.</summary>
    public const int Latest = 7;

    public const int Default = Latest;

    public const int Minimum = 1;
    public const int Maximum = 7;

    public static bool IsSupported(int version) => version is >= Minimum and <= Maximum;
}

/// <summary>
/// A whole BYML document: its root node, the version it declares, and its byte order.
/// </summary>
/// <remarks>
/// <see cref="Byml.FromBinary"/> is enough when only the contents matter. This is what to use
/// to write a document back as it came - the version and byte order are header fields, and a
/// file rewritten at the wrong one is not the file it was.
/// </remarks>
public sealed class BymlFile(Byml root, int version = BymlVersion.Default, bool bigEndian = false)
{
    public Byml Root { get; set; } = root;

    public int Version { get; set; } = version;

    /// <summary>
    /// True when the document is stored big endian, which the magic says: <c>BY</c> big,
    /// <c>YB</c> little.
    /// </summary>
    public bool BigEndian { get; set; } = bigEndian;

    /// <exception cref="InvalidDataException">The data is not a BYML document this can read.</exception>
    public static BymlFile FromBinary(ReadOnlySpan<byte> data) => BymlReader.Read(data);

    public static BymlFile FromFile(string path) => FromBinary(File.ReadAllBytes(path));

    public byte[] Write()
    {
        if (!BymlVersion.IsSupported(Version))
            throw new InvalidOperationException(
                $"BYML version {Version} is outside the supported range " +
                $"{BymlVersion.Minimum}-{BymlVersion.Maximum}.");

        return BymlWriter.Write(Root, Version, BigEndian);
    }

    public void WriteTo(string path) => File.WriteAllBytes(path, Write());
}
