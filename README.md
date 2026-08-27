# WARNO UltiAI MODerator

A Windows C# desktop application that combines either an editable SDK mod or an installed Steam Workshop mod with an editable **UltiAI** or **UltiAIDev** source mod.

[Download the latest Windows release](https://github.com/Samy109/WARNO-UltiAI-MODerator/releases/latest)

## Precedence model

- Editable + UltiAI: each mod is compared with its matching `base.zip`. Only changed source files are applied, and the complete UltiAI file wins when both mods changed the same path.
- Workshop + UltiAI: the Workshop runtime package is retained, including its resource catalog, maps and assets. Freshly generated UltiAI databases are overlaid afterward.
- Compiled `.ndfbin` databases are atomic. They cannot safely be decompiled and merged object-by-object, so an overlapping database is replaced as a whole by UltiAI. The preview explicitly reports these collisions.
- Workshop packages with a different `ModGenVersion` are rejected rather than producing a likely broken mod.

## Use

1. Keep an editable `UltiAI` or `UltiAIDev` folder in `WARNO\Mods` and update it for the current game version.
   Run that mod's `GenerateMod.bat` after its latest source edit; Workshop previews reject stale generated Ulti files.
2. Subscribe to any Workshop mod you want to combine and let Steam finish downloading it.
3. Run `WARNO-UltiAI-MODerator.exe`, choose the other mod and the priority UltiAI variant, then preview.
4. Click **Create combined mod**. The app invokes WARNO's own `CreateNewMod.bat` and `GenerateMod.py`, then verifies every planned compiled output path.

Inputs are never modified. If generation fails, the incomplete new output is preserved for inspection.

## Binary-format limitation

Workshop `.ndfbin` databases and `Catalog.cat` files are compiled, atomic files; WARNO supplies no supported object-level merger for them. When both mods contain the same NDF database, the complete UltiAI database replaces the Workshop database. This guarantees UltiAI precedence but necessarily removes the other mod's changes inside that same database.

For Workshop packages with custom assets, the Workshop `Catalog.cat` is retained so those assets remain registered. Catalog binaries cannot be safely combined, so UltiAI assets that exist only through its own catalog (typically cosmetic branding) may not appear. The application reports this before execution. Gameplay NDF precedence is unaffected.

## Development

Requires the .NET 9 SDK on Windows.

```powershell
dotnet build WarnoModerator.sln
dotnet publish WarnoModerator.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

The experimental 10v10 AI feature is intentionally excluded: it should not be attempted unless a profile-independent, update-safe method is demonstrated.

This is an unofficial community utility and is not affiliated with Eugen Systems.
