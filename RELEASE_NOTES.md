# WARNO UltiAI MODerator v1.0.0

Initial public release, built around the Workshop-first user workflow.

## Primary workflow

- Workshop users can now combine a Workshop mod directly with the Workshop release of UltiAI.
- Editable UltiAI source files are no longer required for normal use.
- The priority selector lists installed Workshop and editable UltiAI variants, with Workshop choices first.
- Workshop + Workshop combinations skip unnecessary source generation and compose installed runtime payloads directly.
- Editable + editable and mixed developer workflows remain available.

## Included

- Self-contained Windows x64 executable; no separate .NET installation required.
- Automatic discovery of installed WARNO Workshop mods.
- Complete UltiAI database precedence for overlapping `.ndfbin` paths.
- Merge preview, ModGen compatibility checks, and final hash verification.
- Military moss-green desktop interface.

## Important limitation

Compiled `.ndfbin` databases and `Catalog.cat` files cannot be safely merged object-by-object. An overlapping NDF database is replaced as a whole by UltiAI. The base Workshop resource catalog is retained to preserve its custom assets, so UltiAI catalog-only cosmetic assets may not appear.

The experimental 10v10 AI feature is not included.
