# WARNO UltiAI MODerator v1.0.0

Initial public release, built around the Workshop-first user workflow.

## Primary workflow

- Workshop users can now combine a Workshop mod directly with the Workshop release of UltiAI.
- Editable UltiAI source files are no longer required for normal use.
- The priority selector lists installed Workshop and editable UltiAI variants, with Workshop choices first.
- Workshop + Workshop combinations use WARNO to generate a valid local-mod identity and current compatibility baseline before composing installed runtime payloads.
- Editable + editable and mixed developer workflows remain available.
- Launches normally without forced elevation. Users whose WARNO folder is protected by Windows can explicitly choose **Run as administrator** when creating a mod.
- Invokes WARNO's bundled `CreateNewMod.py` directly, avoiding `cmd.exe` path failures from the batch wrapper.
- Reports a clear write-access error when Windows Security or filesystem permissions block `WARNO\Mods`.
- Rejects Workshop payloads that do not match the installed game's freshly generated `ModGenVersion`.
- Preserves Eugen's `[Config] ; comment` compatibility section and merges its game-room fingerprints instead of silently dropping them.

## Included

- Self-contained Windows x64 folder distributed as a ZIP; extract the complete folder before running. No separate .NET installation is required.
- Startup failures now display an error and write `%LOCALAPPDATA%\WARNO UltiAI MODerator\startup-error.log` instead of silently exiting.
- Automatic discovery of installed WARNO Workshop mods.
- Complete UltiAI database precedence for overlapping `.ndfbin` paths.
- Merge preview, ModGen compatibility checks, and final hash verification.
- Military moss-green desktop interface.

## Important limitation

Compiled `.ndfbin` databases and `Catalog.cat` files cannot be safely merged object-by-object. An overlapping NDF database is replaced as a whole by UltiAI. The base Workshop resource catalog is retained to preserve its custom assets, so UltiAI catalog-only cosmetic assets may not appear.
