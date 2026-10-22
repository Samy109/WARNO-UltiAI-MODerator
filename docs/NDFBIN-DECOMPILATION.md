# Decompiling and recompiling WARNO `.ndfbin` databases

This document describes how WARNO UltiAI MODerator reads compiled NDF databases (`.ndfbin`, plus `Catalog.cat`, which
uses the same container). It covers turning them into a structured model, merging several mods' changes, and writing a
database the game can load. The format was reverse-engineered from WARNO build 201602 (ModGen 201602). It closely
resembles the Wargame: Red Dragon format that enohka's *moddingSuite* decompiled, but the compression and several value
tags differ.

Implementation: `WarnoModerator.Core/Ndf/`

| File | Role |
|---|---|
| `NdfContainer.cs` | EUG0/CNDF header, LZ4 and zstd bodies, `edat` archive reader, vanilla database lookup |
| `NdfReader.cs` | Decompiler: tables → object graph → `NdfDatabase` |
| `NdfValues.cs` | Immutable, structurally comparable value model |
| `NdfWriter.cs` | Compiler: `NdfDatabase` → tables → zstd container |
| `NdfMerger.cs` | N-way three-way merge against vanilla |

## 1. Why decompile at all

Every Workshop mod ships the **entire** compiled database for each database it touches. For example, its
`Gen/NDF/GFX/Unit.ndfbin` contains all ~3,000 vanilla unit descriptors plus the mod's edits. Copying one mod's file over
another's therefore silently erases every change the other mod made in that database. To combine mods correctly, each
database has to be decompiled. Each mod's actual changes are then found by comparing it with vanilla, and the
non-conflicting changes are combined.

## 2. Container

All integers are little-endian.

| Offset | Type | Meaning |
|---|---|---|
| 0x00 | `char[4]` | `EUG0` |
| 0x04 | `u32` | `1` |
| 0x08 | `char[4]` | `CNDF` |
| 0x0C | `u32` | Body compression: `1` = LZ4 block, `2` = zstd frame |
| 0x10 | `u64` | Offset of the `TOC0` table of contents (logical-file coordinates) |
| 0x18 | `u64` | Header size, always `0x28` |
| 0x20 | `u64` | Total logical size (header + decompressed body) |
| 0x28 | `u32` | Decompressed body size |
| 0x2C | … | Compressed body |

The **logical file** is the first `0x28` header bytes followed by the decompressed body. Every offset inside the file
refers to it. The SDK's mod generator writes large databases with LZ4 and small ones with zstd. The game's own vanilla
databases use zstd at every size, so the writer always emits zstd.

LZ4 is the raw *block* format: a token, an optional literal-length extension, literals, a 2-byte offset and an optional
match-length extension, with no frame header. The decoder is about 25 lines (`Lz4Block`).

## 3. Table of contents

`TOC0`, a `u32` count (9), then 24-byte entries: `char[4] name`, `u32 0`, `u64 offset`, `u64 size`.

| Table | Contents |
|---|---|
| `OBJE` | Every object, in index order (see §4) |
| `TOPO` | `u32` indices of the **top-level** objects, in source order (not sorted) |
| `CHNK` | `u32 start, u32 count` covering the object range (`0, objectCount`) |
| `CLAS` | Class names: repeated `u32 length, bytes` |
| `PROP` | Properties: repeated `u32 length, bytes, u32 classIndex`. The class is the **declaring** class, which can be a base class of the objects that use the property |
| `STRG` | String literals and file paths: repeated `u32 length, bytes` |
| `TRAN` | Path segments used by the name trees (`$`, `GFX`, `Unit`, `Descriptor_Unit_…`) |
| `IMPR` | Import name tree: references to objects exported by *other* databases |
| `EXPR` | Export name tree: names of objects this database exports |

Strings are read as Latin-1 so every byte round-trips unchanged.

### Name trees (`IMPR`, `EXPR`)

Each node is `u32 tranIndex, u32 target, u32 childCount, u32 childOffset[childCount]`. A child sits at *(address of
`childOffset[0]`) + `childOffset[i]`*. Nodes are laid out in pre-order. Joining the segments from the root gives a full
path such as `$/GFX/Unit/Descriptor_Unit_FOB_BEL`. `target` is `0xFFFFFFFF` for interior nodes. In `EXPR` it is the
exported object's index; in `IMPR` it is the import index used by import references. An empty tree has size 0.

## 4. Objects and values

`OBJE` is a sequence of objects:

```
u32 classIndex
repeat { u32 propertyIndex, value }
u32 0xABABABAB            // end of object
```

Each value is a `u32` tag followed by its payload:

| Tag | Meaning | Payload |
|---|---|---|
| `0x00` | bool | 1 byte |
| `0x01` | int8 | 1 |
| `0x18` / `0x19` | int16 / uint16 | 2 |
| `0x02` / `0x03` | int32 / uint32 | 4 |
| `0x05` | float32 | 4 |
| `0x0D` | color (RGBA) | 4 |
| `0x07` | string literal | `u32` index into `STRG` |
| `0x1C` | file path (`GameData:/…`) | `u32` index into `STRG` |
| `0x06` | float64 | 8 |
| `0x13`, `0x1D`, `0x1F`, `0x21` | 8-byte scalars (localisation hash, int2, float2…) | 8 |
| `0x0B`, `0x0F` | float3 / int3 vectors | 12 |
| `0x0C`, `0x10`, `0x1A`, `0x25` | float4, GUID, 16-byte hashes | 16 |
| `0x08`, `0x14` | wide string / blob | `u32 length` + bytes |
| `0x09` | reference | `0xAAAAAAAA, u32 importIndex`, **or** `0xBBBBBBBB, u32 objectIndex, u32 classIndex` |
| `0x11` | list | `u32 count` + values |
| `0x12` | map | `u32 count` + (key value, value value) pairs |
| `0x22` | pair | value, value |

An object reference of `0xFFFFFFFF / 0xFFFFFFFF` is **nil**. Only `0x07`, `0x1C` and `0x09` point into other tables. Every
other payload is copied verbatim. The fixed sizes were confirmed because every Workshop and vanilla database parses to
the exact end of its object table; a wrong size would desynchronise the stream.

## 5. From object graph to model

The raw object table is a graph that refers to objects by index. The decompiler turns it into a tree that stays stable
across compilations:

* **Exported top-level objects** are identified by their export path. A reference to one becomes `NdfExportRef(path)`.
* **Imports** become `NdfImport(path)` through the `IMPR` tree.
* **Every other object is inlined** as an `NdfObject(className, fields)` wherever it is referenced. Its index depends on
  the compiler, so it cannot identify the object across mods.
* Two kinds of objects are referenced from many places:
  * *Shared nested objects*: the compiler emits a module descriptor once and references it from many units.
  * *Anonymous top-level objects*: private (non-exported) constants listed in `TOPO`.

  The decompiler builds each such object once, so every reference shares the same instance and memory stays bounded.
  It also records them (`SharedObjects`, and top-level entries without an export path), so the compiler can share them
  again.
* Properties are keyed by name. When a property is declared on a base class, the declaring class is recorded in
  `PropertyOwners` so it can be written back the same way.

Every value is immutable and has structural equality with a cached hash. Comparing two databases is therefore cheap:
unchanged sub-trees compare equal by hash, and only real differences are walked.

## 6. Compiling the model back

`NdfWriter` rebuilds every table from scratch:

1. Walk the top-level objects in `TOPO` order and assign indices in pre-order: each object, then its inline children.
   This matches the compiler, which always places a nested object after its first referrer. Objects that were shared in
   any input are emitted once and referenced from every use. A use of an anonymous constant points at its top-level
   entry.
2. Encode the objects. Class, property, string, path-segment and import tables are filled on first use.
3. Rebuild the `IMPR` and `EXPR` trees from the paths, write `TOPO`, `CHNK` and the TOC, then compress the body with zstd.

**Validation:** all 653 databases available locally decompile, recompile and decompile again to an identical model.
They are the 475 Workshop databases plus the 178 vanilla databases in `Glad.dat`.

## 7. The vanilla baseline

A three-way merge needs the unmodified database for the same build. WARNO keeps one per build in
`Data/PC/<ModGenVersion>/Glad.dat`, an `edat` archive:

* The header is `edat`, then `u32` version, `u32 dictionaryOffset`, `u32 dictionaryLength`, `u64 filesOffset`, `u64 filesLength`.
* The dictionary is a prefix trie of records:
  * `u32 headerLength` (`0` = file)
  * `u32 spanToNextSibling` (`0` = last sibling)
  * then either a NUL-terminated directory-name prefix, or a file record: `u64 offset, u64 size, 16-byte checksum`,
    NUL-terminated name.
* A full path is the concatenation of the prefixes along the way, e.g. `NDF/GFX/Unit.ndfbin`.

A mod's `Gen/NDF/GFX/Unit.ndfbin` maps to the archive entry `NDF/GFX/Unit.ndfbin`. Asset catalogs have no vanilla
counterpart; see §8.

## 8. Merging

`NdfMerger.Merge(vanilla, variants)` takes each mod's full database, highest priority first. It merges recursively. For
every value it collects the mods whose value differs from vanilla:

* No mod changed it: keep vanilla.
* Every mod that changed it agrees: take that value.
* Otherwise, when the values have compatible structure, merge one level deeper:
  * **Objects of the same class**, field by field.
  * **Lists**, keyed by element identity, so additions and removals from different mods combine. The key is the class
    plus `_ShortDatabaseName`, `Name` or `DescriptorId`, or the value itself for strings, references and scalars. Lists
    whose elements can't be keyed uniquely merge position by position when every version has the same length.
  * **Maps**, by key.
  * **Pairs**, element by element.
* Anything else is a **value conflict**. The highest-priority mod's value wins, and the conflict is reported with the
  value path, the winner and the overridden mods.

Exports added by mods are inserted after their nearest preceding export, preserving each mod's own ordering. If a mod
deleted an export that merged content still references, the export is restored and reported, so the output never
contains a dangling reference.

Databases WARNO doesn't ship, such as `Catalog.cat`, merge **without a base**. Every mod's content counts as an addition,
so each mod's asset registrations are unioned by asset path. For example, merging four mods' catalogs gives a single
catalog that registers every mod's assets, with no conflicts.

## 9. Known limits

* The tags `0x13`, `0x1D`, `0x1F`, `0x21` and `0x25` are copied verbatim. Their sizes are certain but their exact
  meaning is not.
* Value conflicts are resolved purely by priority. Two mods that rebalance the same unit in different ways produce a
  primary-mod value for each conflicting field, not a coherent design.
* The writer emits zstd. The game reads zstd for every vanilla database, but every merged output should still be
  playtested in-game.
