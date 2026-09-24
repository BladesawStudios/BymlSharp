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
