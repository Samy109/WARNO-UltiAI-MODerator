# WARNO UltiAI MODerator

A Windows desktop application designed primarily for Workshop users. It combines an installed Steam Workshop mod directly with the Workshop version of **UltiAI**, without requiring editable mod source files.

Editable SDK mods and UltiAIDEV remain supported as optional developer workflows.

[Download the latest Windows release](https://github.com/Samy109/WARNO-UltiAI-MODerator/releases/latest)

## Precedence model

- Workshop + Workshop UltiAI: both installed compiled payloads are composed directly. No editable source is required.
- Editable + editable UltiAI: each mod is compared with its matching `base.zip`. Only changed source files are applied, and the complete UltiAI file wins when both mods changed the same path.
- Mixed source/Workshop combinations are also supported; the editable side is generated before compiled composition.
- Compiled `.ndfbin` databases are atomic. They cannot safely be decompiled and merged object-by-object, so an overlapping database is replaced as a whole by UltiAI. The preview explicitly reports these collisions.
- Workshop packages with a different `ModGenVersion` are rejected rather than producing a likely broken mod.

## Use

1. Subscribe to **UltiAI** in the Steam Workshop and let Steam finish downloading it.
2. Subscribe to the other Workshop mod you want to combine.
3. Run `WARNO-UltiAI-MODerator.exe`, choose the other mod and the priority UltiAI variant, then preview.
4. Click **Create combined mod**. The app creates a local combined mod and verifies every planned output path.

No editable files are needed for the normal Workshop workflow. If you are developing from source, editable mods found under `WARNO\Mods` are offered alongside Workshop versions; stale editable generations are rejected.

The app launches without requesting administrator access. If WARNO is installed under `Program Files`, Windows may prevent creation inside `WARNO\Mods`; in that case, close the app, right-click `WARNO-UltiAI-MODerator.exe`, and choose **Run as administrator** before creating the combined mod. The app invokes WARNO's bundled Python creation tool directly.

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

This is an unofficial community utility and is not affiliated with Eugen Systems.
