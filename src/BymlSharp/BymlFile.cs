namespace BymlSharp;

public static class BymlVersion
{
    public const int WiiU = 2;

    public const int Switch = 3;

    public const int Latest = 7;

    public const int Default = Latest;

    public const int Minimum = 1;
    public const int Maximum = 7;

    public static bool IsSupported(int version) => version is >= Minimum and <= Maximum;
}

public sealed class BymlFile(Byml root, int version = BymlVersion.Default, bool bigEndian = false)
{
    public Byml Root { get; set; } = root;

    public int Version { get; set; } = version;
    public bool BigEndian { get; set; } = bigEndian;

    public bool ShareIdenticalContainers { get; set; }

    public static BymlFile FromBinary(ReadOnlySpan<byte> data) => BymlReader.Read(data);

    public static BymlFile FromFile(string path) => FromBinary(File.ReadAllBytes(path));

    /// <summary>
    /// A document from YAML text. Given the file the text was made from, it also takes that file's version, byte
    /// order and layout (see <see cref="AdoptLayoutFrom"/>), so unedited text writes back the original bytes.
    /// </summary>
    public static BymlFile FromYaml(string yaml, BymlFile? original = null)
    {
        BymlFile file = new(Byml.FromYaml(yaml));
        if (original is not null) file.AdoptLayoutFrom(original);
        return file;
    }

    /// <summary>
    /// Takes the original's version, byte order and writer settings, and shares the nodes the original shared,
    /// and aligns the binary blobs it aligned, wherever this document still has the same content at the same
    /// place. YAML without anchors can't say any of that. Edited parts that no longer match stay as they are.
    /// </summary>
    public void AdoptLayoutFrom(BymlFile original)
    {
        ArgumentNullException.ThrowIfNull(original);
        Version = original.Version;
        BigEndian = original.BigEndian;
        ShareIdenticalContainers = original.ShareIdenticalContainers;
        BymlSharing.Adopt(Root, original.Root);
    }

    public byte[] Write()
    {
        if (!BymlVersion.IsSupported(Version))
            throw new InvalidOperationException(
                $"BYML version {Version} is outside the supported range " +
                $"{BymlVersion.Minimum}-{BymlVersion.Maximum}.");

        return BymlWriter.Write(Root, Version, BigEndian, ShareIdenticalContainers);
    }

    public void WriteTo(string path) => File.WriteAllBytes(path, Write());
}
