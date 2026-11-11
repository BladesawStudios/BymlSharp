namespace BymlSharp;

internal static class BymlYaml
{
    public static string Write(Byml root, bool anchors)
        => BymlYamlWriter.TryWrite(root, anchors) ?? BymlYamlLegacy.Write(root, anchors);

    public static Byml Read(string yaml)
    {
        try
        {
            return BymlYamlReader.Read(yaml);
        }
        catch (BymlYamlReader.Unsupported)
        {
            return BymlYamlLegacy.Read(yaml);
        }
    }
}
