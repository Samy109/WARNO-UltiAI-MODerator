# WARNO UltiAI MODerator

A Windows desktop application designed primarily for Workshop users. It combines **any** number of installed Steam Workshop mods into one local mod, without requiring editable mod source files. UltiAI is listed first and selected as the primary mod by default, but it is no longer required.

Editable SDK mods and UltiAIDEV remain supported as optional developer workflows.

[Download the latest Windows release](https://github.com/Samy109/WARNO-UltiAI-MODerator/releases/latest)

## How mods are combined

- **Delta merging of compiled databases.** Every Workshop mod ships complete `.ndfbin` databases, so replacing one mod's file with another's would erase the first mod's changes. The app decompiles each database and compares it with the vanilla database of the same WARNO build (`Data\PC\<ModGenVersion>\Glad.dat`) to find exactly what each mod changed. It then combines those changes value by value and recompiles a single database. See [docs/NDFBIN-DECOMPILATION.md](docs/NDFBIN-DECOMPILATION.md).
- **Priority order.** Mods are listed in priority order. The top mod is the **primary** mod: when several mods change the same value differently, the higher mod wins and the preview lists each value conflict. Changes that don't collide are all kept.
- **Asset catalogs** (`Catalog.cat`) are merged the same way, so every mod's custom textures, meshes and sounds stay registered. `DeclaredFiles.txt` and `UsedFiles.txt` become the union of all mods' entries.
- **Other files** (assets, maps, videos) are atomic. A file only one mod provides is always kept; when several mods provide the same file, the highest-priority mod's copy is used. Generator build caches are taken from the mod with the largest compiled payload.
- **Editable SDK mods** are compared with their matching `base.zip`; changed source files are applied in priority order and compiled by WARNO, then delta merged with any Workshop mods.
- Workshop packages with different `ModGenVersion`s are rejected rather than producing a likely broken mod.

## Use

1. Subscribe to the Workshop mods you want to combine (for example **UltiAI** and an overhaul mod) and let Steam finish downloading them.
2. Download the release ZIP, extract the entire folder, then run `WARNO-UltiAI-MODerator.exe`. Do not run the executable from inside the ZIP.
3. Under **Mods to combine**, pick a mod and click **Add mod** for each one. UltiAI appears first in the list and is added automatically. Use **Make primary**, **Move up** and **Move down** to set the priority order; the top mod is primary. The merge preview updates automatically and shows every delta-merged database and value conflict.
4. Click **Create as New**. The app creates a local combined mod, verifies every planned output path, and records one compact fingerprint for each source mod.

When you select the same source mods again (or launch the app with an existing combined mod), choose an output under **Existing merge** to update or rebuild it. The app checks that output and reports source updates, missing or altered output, changes to WARNO build data, or a failed check. **Update and Rebuild** is available when sources changed; **Rebuild Existing** remains available even when they did not. To keep the selected merge and make a separate one, use the suggested unused **New mod name** or enter another unused name, then click **Create as New**. Each output can be selected and rebuilt independently.

Rebuilding checks the inputs again and safeguards the previous combination until generation, verification, and saving the update record succeed. If source files or WARNO build data change during the merge, it stops and restores the previous combination. Older records require one rebuild to begin tracking output integrity.

Combined mods made with v1.0.0 under the default generated name are recognized automatically. They require one initial tracked rebuild; manual deletion is not required.

WARNO itself generates the combined mod's local identity and compatibility baseline. The app rejects Workshop payloads whose `ModGenVersion` does not match the installed game, preventing game-room version mismatch packages.

No editable files are needed for the normal Workshop workflow. If you are developing from source, editable mods found under `WARNO\Mods` are offered alongside Workshop versions; editable inputs are regenerated in the combined output without modifying their source folders. Mixed previews are marked provisional until generation produces the final compiled plan.

The app launches without requesting administrator access. If WARNO is installed under `Program Files`, Windows may prevent creation inside `WARNO\Mods`; in that case, close the app, right-click `WARNO-UltiAI-MODerator.exe`, and choose **Run as administrator** before creating the combined mod. The app invokes WARNO's bundled Python creation tool directly.

Inputs are never modified. Failed or cancelled output is moved to a separately named incomplete-output folder for inspection. A rebuild restores each previous output independently. If a file lock prevents recovery, the app reports the backup paths; close programs using those files and refresh mods to retry. Interrupted builds are detected at startup or refresh and can be recovered.

## Preview, reports, and cancellation

- Search preview rows by file or value path, decision, winning mod, or reason, or select **Conflicts only** to focus on value conflicts and overridden files. Each row displays a color-coded **Decision** badge (`Delta Merged`, `Value Conflict`, `Priority Override`, `Single Mod`, `Identical`).
- **Export report** saves the priority order, complete plan, every value conflict, summary breakdown, warnings, and diagnostic session log as JSON, regardless of the active filter.
- Editable/Workshop previews are marked provisional. After generation, the final authoritative plan drives merging, copying, manifest fingerprints, verification, and the displayed report.
- Combined mods made by v1.x are recognized and can be rebuilt with v2's delta merging.
- **Cancel** stops input checks, planning, copying, or SDK generation. Rebuild recovery finishes before the app returns to idle. Closing the window during an operation requests cancellation; close it again once recovery finishes.
- Compiled inputs must have a readable ModGen revision and a manifest fingerprint for every selected NDF database. Refresh incomplete Workshop downloads before retrying.

## Limitations

- Value conflicts are resolved by priority alone. Two mods that rebalance the same unit differently produce the primary mod's value for each conflicting field, which can make an inconsistent design. Review the **Conflicts only** view.
- Merged databases are written by this app's own compiler rather than Eugen's. Every database round-trips through it losslessly, but playtest each combination in-game.
- If WARNO's vanilla databases can't be found for the installed build, overlapping databases fall back to whole-file priority, and the preview says so.

## Development

Requires the .NET 9 SDK on Windows when building from source. The release package is self-contained and does not require a separate .NET installation.

```powershell
dotnet build WarnoModerator.sln
dotnet publish WarnoModerator.App -c Release -r win-x64 --self-contained true
```

This is an unofficial community utility and is not affiliated with Eugen Systems.
