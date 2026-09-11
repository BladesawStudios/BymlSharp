# BymlSharp

A C# reader for **BYML**, the binary document format recent Nintendo titles use for almost
everything that is not geometry or audio — actor placement, actor parameters, game
parameter tables.

```csharp
Byml root = Byml.FromFile("Dungeon000_Static.byml");

foreach (Byml actor in root["Actors"]!.Array)
{
    string name = actor["Gyaml"]!.String;
    var t = actor["Translate"]?.Array;
    ulong id = actor["Hash"]!.UInt64;
}
```

Reads and writes, with no dependencies. Containers are handed out as live collections, so a
tree read from a file can be edited and written straight back:

```csharp
BymlFile doc = BymlFile.FromFile("Dungeon000_Static.byml");
doc.Root["Actors"]!.AsArray.Add(Byml.Map(new Dictionary<string, Byml>
{
    ["Gyaml"]     = "DgnObj_Hrl_Box4x4x4Top_01",
    ["Hash"]      = 1234567890123456789UL,
    ["Translate"] = Byml.Array([0f, 26f, -282f]),
}));
doc.WriteTo("Dungeon000_Static.byml");
```

`BymlFile` carries the version and byte order alongside the root, so a document written back
is the document it was. `Byml.FromFile` is the shortcut when only the contents matter.

## Input must already be decompressed

Shipped documents are zstd compressed (`.byml.zs`, `.bgyml`), and *Tears of the Kingdom*
compresses most of them against one of three dictionaries it carries in
`Pack/ZsDic.pack.zs`. Which dictionary applies depends on where the file sits in romfs, so
that belongs to whatever knows the romfs layout rather than here.

## Versions and byte order

Versions 1 to 7 are read and written, big endian and little. The version is a header field
and in practice says which title wrote the document rather than changing the layout — the
containers, the tables and the node encoding are the same across the range. Established
readers pass it through without gating node types on it, and this does the same.

What does vary is which types a title happens to use: the hash-map containers only turn up in
version 7. The format will let you write one into an older version; a reader expecting that
version may not thank you for it.

## The format

A header names three offsets — a table of key names, a table of string values, and the root
node. Every node is a type byte plus four bytes of payload: values of four bytes or fewer sit
in the payload, and anything larger, containers included, puts an offset there instead.

Supported node types: `Map`, `Array`, `HashMap32`, `HashMap64`, `String`, `Binary`,
`BinaryAligned`, `Bool`, `Int`, `UInt`, `Int64`, `UInt64`, `Float`, `Double`, `Null`.
Both byte orders are read; the magic (`YB` or `BY`) says which.

A detail worth knowing when reading placement files: the same field is not always the same
type — a value is a float in one actor and an integer in the next. `AsNumber()` reads any of
them, which is usually what you want over the exact-typed accessors.

Writing sorts the key and string tables ordinally and sorts map entries by key, because the
game binary-searches both. Nodes are not deduplicated, so a written file can be slightly
larger than the original even when it holds the same tree.

## Checked against

Every `.bgyml` and placement file to hand from *Tears of the Kingdom* — 38 of them — parses,
writes and parses back to an identical tree, and 35 come back byte-identical to the original.
A tree using every node type round trips at all seven versions in both byte orders.

## Build

```bash
dotnet build BymlSharp.sln -c Release
```

## Licence

AGPL-3.0-or-later. See [license.md](license.md).
