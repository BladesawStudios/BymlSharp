namespace BymlSharp;

// YAML goes through hand-written text code for the documents this library writes and the forms editors keep
// them in; anything else (block scalars, multi-line values, odd tags) falls back to the YamlDotNet-based
// implementation, which stays the reference for what the text means.
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
