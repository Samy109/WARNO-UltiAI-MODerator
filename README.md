# WARNO UltiAI MODerator

A Windows desktop application designed primarily for Workshop users. It combines an installed Steam Workshop mod directly with the Workshop version of **UltiAI**, without requiring editable mod source files.

Editable SDK mods and UltiAIDEV remain supported as optional developer workflows.

[Download the latest Windows release](https://github.com/Samy109/WARNO-UltiAI-MODerator/releases/latest)

## Precedence model

- Workshop + Workshop UltiAI: WARNO first generates a current local-mod compatibility manifest, then both installed compiled payloads are composed directly. No editable source is required.
- Editable + editable UltiAI: each mod is compared with its matching `base.zip`. Only changed source files are applied, and the complete UltiAI file wins when both mods changed the same path.
- Mixed source/Workshop combinations are also supported; the editable side is generated before compiled composition.
- Compiled `.ndfbin` databases are atomic. They cannot safely be decompiled and merged object-by-object, so an overlapping database is replaced as a whole by UltiAI, except `Gen/NDF/UI/Components.ndfbin`, which comes from the other mod when both provide it. The preview explicitly reports these collisions.
- Workshop packages with a different `ModGenVersion` are rejected rather than producing a likely broken mod.

## Use

1. Subscribe to **UltiAI** in the Steam Workshop and let Steam finish downloading it.
2. Subscribe to the other Workshop mod you want to combine.
3. Download the release ZIP, extract the entire folder, then run `WARNO-UltiAI-MODerator.exe`. Choose the other mod and the priority UltiAI variant, then preview. Do not run the executable from inside the ZIP.
4. Click **Create as New**. The app creates a local combined mod, verifies every planned output path, and records one compact fingerprint for each source mod.

When you select the same source mods again, the app checks the installed inputs and existing combined output. A visible status reports detected source updates, missing or altered output, changes to WARNO build data, or a failed check. **Update and Rebuild** is available when sources changed; **Rebuild Existing** remains available even when they did not. **Create as New** stays unavailable for an existing combination.

Rebuilding checks the inputs again and safeguards the previous combination until generation, verification, and saving the update record succeed. If source files or WARNO build data change during the merge, it stops and restores the previous combination. Older records require one rebuild to begin tracking output integrity.

Combined mods made with v1.0.0 under the default generated name are recognized automatically. They require one initial tracked rebuild; manual deletion is not required.

WARNO itself generates the combined mod's local identity and compatibility baseline. The app rejects Workshop payloads whose `ModGenVersion` does not match the installed game, preventing game-room version mismatch packages.

No editable files are needed for the normal Workshop workflow. If you are developing from source, editable mods found under `WARNO\Mods` are offered alongside Workshop versions; stale editable generations are rejected.

The app launches without requesting administrator access. If WARNO is installed under `Program Files`, Windows may prevent creation inside `WARNO\Mods`; in that case, close the app, right-click `WARNO-UltiAI-MODerator.exe`, and choose **Run as administrator** before creating the combined mod. The app invokes WARNO's bundled Python creation tool directly.

Inputs are never modified. If initial creation fails, the incomplete new output is preserved for inspection. If an update fails, the last working combined mod is restored.

## Binary-format limitation

Workshop `.ndfbin` databases and `Catalog.cat` files are compiled, atomic files; WARNO supplies no supported object-level merger for them. When both mods contain the same NDF database, the complete UltiAI database replaces the Workshop database, except `UI/Components.ndfbin`. For that UI database, the other mod takes precedence to preserve its custom interface and texture registrations. If only UltiAI supplies it, UltiAI's version is retained. The compatibility manifest follows the selected UI payload.

Combined mods may retain vanilla end-game difficulty labels. Additional roles such as Siege require an end-game summary playtest with the other mod's UI. Rebuild existing combinations after installing the revised v1.2.0 package.

For Workshop packages with custom assets, the Workshop `Catalog.cat` is retained so those assets remain registered. Catalog binaries cannot be safely combined, so UltiAI assets that exist only through its own catalog (typically cosmetic branding) may not appear. The application reports this before execution. Gameplay NDF precedence is unaffected.

## Development

Requires the .NET 9 SDK on Windows when building from source. The release package is self-contained and does not require a separate .NET installation.

```powershell
dotnet build WarnoModerator.sln
dotnet run --project WarnoModerator.Tests -c Release
dotnet publish WarnoModerator.App -c Release -r win-x64 --self-contained true
```

This is an unofficial community utility and is not affiliated with Eugen Systems.
