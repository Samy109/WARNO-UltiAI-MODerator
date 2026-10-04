# WARNO UltiAI MODerator v1.4.0

- Fix compatibility fingerprints so every selected compiled database uses its winning input's manifest entry. Editable inputs use the freshly generated manifest. Per-mod-name databases (`Localisation/<Mod>`, `ResourcePacks/<Mod>`), which WARNO never fingerprints, no longer block combining Workshop mods such as Honor Or Death.
- Fix a crash when selecting a mod pair that already has a combined output. Source-check progress is now reported on the UI thread.
- Restore source and runtime backups independently after failed or cancelled builds. Preserve incomplete output and provide recovery paths when files are locked.
- Detect interrupted builds at startup or refresh and offer recovery; persist the commit before removing backups.
- Safely ignore malformed tracking records and reject compiled inputs with missing compatibility revisions or selected database fingerprints.
- Keep scanning, planning, hashing, and building off the UI thread. Add cancellation, including stopping and awaiting SDK processes.
- Use one shared planner for file selection, catalog fallback, copying, manifest generation, verification, and final reporting. Mark mixed-source previews as provisional until fresh generation.
- Add path search, a differing-conflicts filter, identical-overlap labels, and JSON report export.
- Preserve v1.3.0's existing-merge selector and the ability to create several independently rebuildable outputs from the same inputs.
- Consolidate create/rebuild workflows and remove unused code. Remove the test project after verification.

Extract the entire Windows x64 ZIP before running the executable. Rebuild existing combinations to apply these fixes. Compiled databases remain atomic; in-game compatibility and additional AI role labels still require playtesting.
---

# WARNO UltiAI MODerator v1.3.0

- Choose an existing combined mod to update or rebuild, even when several outputs use the same source mods.
- Create a fresh combined mod from the same sources with a separate name. The app suggests the next unused name and leaves previous outputs intact.
- Keep the existing rebuild safeguard and verification for the selected output.

Extract the entire Windows ZIP before running the app.

---

# WARNO UltiAI MODerator v1.2.0

- Preserve the other mod's UI components when both mods supply them, protecting custom division emblem registrations such as Spearhead Reforged's.
- Keep UltiAI precedence for remaining compiled databases; use UltiAI UI components when the other mod has none.
- Align merge previews, output verification, and compatibility entries with the selected UI components.
- Rebuild existing combinations after installing this replacement v1.2.0 ZIP. End-game labels may use the other mod's defaults; additional roles such as Siege still need an end-game playtest.
- Rebuild existing combinations even when no source updates are detected—no manual deletion needed.
- See clear status messages for source updates, missing or changed output, and failed checks.
- Detect changed WARNO build data and offer a rebuild.
- Recheck inputs during merging and restore the previous combination if a rebuild fails, including final verification or saving its update record.
- Preserve compatibility with previously tracked combinations.

Extract the entire Windows ZIP before running the app.

---

# WARNO UltiAI MODerator v1.1.1

This patch fixes mixed editable/Workshop combinations that could fail verification on stale generated audio files.

## Fixed

- Builds the compiled verification plan from the freshly generated payload instead of an older preview inventory.
- Prevents false `The staged package is missing` errors for stale voice assets such as `select_19.ess`.
- Continues to verify every file actually composed into the package, including UltiAI precedence and fallback resource catalogs.

---

# WARNO UltiAI MODerator v1.1.0

This release adds streamlined change detection and in-place rebuilding for combined mods.

## Updating combined mods

- Stores one compact SHA-256 fingerprint per source mod; individual filenames are not retained in the combination record.
- Checks existing combinations automatically using the existing determinate progress bar.
- Enables **Update and Rebuild** only when at least one selected source mod has changed.
- Lists the affected source mods in the rebuild confirmation popup.
- Disables **Create as New** after that source-mod combination has already been created.
- Safeguards the current source and runtime outputs during rebuilding and restores both automatically if the rebuild fails.
- Recognizes v1.0.0 combinations that use the default generated name and offers a one-time tracked rebuild instead of requiring manual deletion.

## Interface

- Renames **Create combined mod** to **Create as New**.
- Reuses the same progress bar for source checking, initial creation, rebuilding, and final verification.
- Shows concise tooltips explaining why create or update is unavailable.

---

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
- Determinate progress bar with live stage text and percentage during generation, composition, and verification.

## Important limitation

Compiled `.ndfbin` databases and `Catalog.cat` files cannot be safely merged object-by-object. An overlapping NDF database is replaced as a whole by UltiAI. The base Workshop resource catalog is retained to preserve its custom assets, so UltiAI catalog-only cosmetic assets may not appear.
