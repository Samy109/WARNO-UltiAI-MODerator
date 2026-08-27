# WARNO UltiAI MODerator v1.0.0

Initial public release.

## Included

- Self-contained Windows x64 executable; no separate .NET installation required.
- Automatic discovery of editable WARNO SDK mods and installed Steam Workshop mods.
- Selectable UltiAI or UltiAIDEV priority source.
- Source-delta merging for editable mods.
- Compiled runtime composition for Workshop-only mods.
- Complete UltiAI database precedence for overlapping `.ndfbin` paths.
- Merge preview, ModGen compatibility checks, stale-generation detection, and final hash verification.
- Military moss-green desktop interface.

## Important limitation

Compiled `.ndfbin` databases and `Catalog.cat` files cannot be safely merged object-by-object. An overlapping NDF database is replaced as a whole by UltiAI. The Workshop resource catalog is retained to preserve its custom assets, so UltiAI catalog-only cosmetic assets may not appear.

The experimental 10v10 AI feature is not included.
