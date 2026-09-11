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

Read only, and no dependencies. A document is parsed once into managed nodes and then walked,
which suits how these files get used — opened, read through, dropped.

## Input must already be decompressed

Shipped documents are zstd compressed (`.byml.zs`, `.bgyml`), and *Tears of the Kingdom*
compresses most of them against one of three dictionaries it carries in
`Pack/ZsDic.pack.zs`. Which dictionary applies depends on where the file sits in romfs, so
that belongs to whatever knows the romfs layout rather than here.

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

## Build

```bash
dotnet build BymlSharp.sln -c Release
```

## Licence

AGPL-3.0-or-later. See [license.md](license.md).
