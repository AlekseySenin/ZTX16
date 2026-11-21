# ZTX16 — Project/Cartridge Decisions Confirmed During Finalization

These decisions were explicitly confirmed immediately before the implementation package was created.

## Project

- Project is a structured persistent directory.
- Project Config is mandatory.
- Valid Config identifies the directory as a ZTX16 Project.
- Config maps directories to resource/source types.
- New projects receive a standard Config and standard structure.
- Standard structure is a default convention, not a hardcoded platform restriction.
- Developers may change root mappings through Config.
- Nested directories are allowed.
- Missing resource directories are normal and created on demand.
- Unknown/unrelated files are allowed, ignored by ZTX16 and excluded from Cartridge.
- Default code directory is conceptually `code/`, while language-specific settings live in Config.

## Cartridge

- Cartridge contains the compiled executable representation plus packaged project resources/assets and required metadata in one self-contained packed file.
- Runtime loads the cartridge into the runtime environment; the exact memory-loading strategy is implementation-defined.
- Cartridge can be extracted/imported back into a normal Project.
- Extraction recreates Project Config and the directory structure dictated by it.
- Project ↔ Cartridge is conceptually reversible at the project-content level.
