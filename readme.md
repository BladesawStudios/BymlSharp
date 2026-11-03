# BymlSharp

A C# library for working with `BYML` files.

## Build

```bash
dotnet build BymlSharp.sln -c Release
```

## Performance

Measured on a Ryzen 9 9900X3D with 32 GB of RAM, .NET 10, Release build, one thread. Files come from a
TotK 121 romfs dump. Each figure is the median of 7 to 9 timed runs after warm-up, taken with
`GC.Collect()` between runs. "Before" is the library before the optimization work, run through the same harness
on the same files. Every file in every set still rewrites byte for byte.

### Scene files: 3000 `.bcett` files, 64.6 MiB

| Operation | Before | After | Speedup | Allocated before | Allocated after |
|---|---|---|---|---|---|
| Read | 1991 ms | 246 ms | 8.1x | 3385 MiB | 890 MiB |
| Write | 706 ms | 377 ms | 1.9x | 1790 MiB | 245 MiB |
| `ToYaml` | 2529 ms | 392 ms | 6.4x | 2106 MiB | 229 MiB |
| `FromYaml` | 4261 ms | 483 ms | 8.8x | 4658 MiB | 1194 MiB |

### Database files: 21 `RSDB` files, 11.2 MiB

| Operation | Before | After | Speedup |
|---|---|---|---|
| Read | 264 ms | 108 ms | 2.5x |
| Write | 124 ms | 73 ms | 1.7x |
| `ToYaml` | 427 ms | 61 ms | 7.0x |
| `FromYaml` | 901 ms | 114 ms | 7.9x |

### Small parameter files: 4000 `.bgyml` files, 4.1 MiB

| Operation | Before | After | Speedup |
|---|---|---|---|
| Read | 44.8 ms | 16.1 ms | 2.8x |
| Write | 74.3 ms | 45.5 ms | 1.6x |
| `ToYaml` | 76.7 ms | 27.3 ms | 2.8x |
| `FromYaml` | 101.0 ms | 14.2 ms | 7.1x |

### Effect files: 1717 documents, 567.5 MiB, mostly binary blobs

| Operation | Before | After | Speedup |
|---|---|---|---|
| Read | 83.1 ms | 79.6 ms | 1.0x |
| Write | 106.0 ms | 63.5 ms | 1.7x |

## Licence

AGPL-3.0-or-later. See [license.md](license.md).
