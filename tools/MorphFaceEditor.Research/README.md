# MorphFaceEditor shader research tool

This console keeps reverse-engineering code out of the production reader and renderer. It is intentionally a developer tool, not a shipped application dependency.

## Pass-1 commands

Audit the nine canonical native and cross-game `GlobalMorphs` corpora without
including nested or unrelated fixture packages:

```powershell
dotnet run --project tools/MorphFaceEditor.Research -c Release -- corpus-audit "tests/Global Morphs" <output.json>
```

Generate the compact direction-specific asset reconciliation catalogue derived
from those canonical source/port pairs:

```powershell
dotnet run --project tools/MorphFaceEditor.Research -c Release -- corpus-reconciliation "tests/Global Morphs" <output.json>
```

Locate exports by a case-insensitive name fragment:

```powershell
dotnet run --project tools/MorphFaceEditor.Research -c Release -- locate-export <game-root> <name-fragment>
```

Trace a skeletal mesh's material slots, parent chains, cooked material properties, uniform bindings, and compiled shader-map inventory without reading shader bytecode:

```powershell
dotnet run --project tools/MorphFaceEditor.Research -c Release -- trace-material <package.pcc> <mesh-name> <report.md>
```

Generated reports belong under `.codex-temp/shader-research/` and should not be committed. Preserve durable conclusions in production code, regression tests, or deliberately maintained documentation.

## Pass-2 command

Extract every shader for one vertex factory, preserving the cooked bytecode,
disassembly, shader GUID, SHA-256, instruction count, source cache, and manifest:

```powershell
dotnet run --project tools/MorphFaceEditor.Research -c Release -- dump-shaders <package.pcc> <skeletal-mesh-name> <slot> <vertex-factory> <output-directory>
```

The command checks a package's local seek-free shader cache first and then the
game reference cache. Generated `.bin`, `.asm`, and `manifest.tsv` artifacts
belong under `.codex-temp/shader-research/`; they should be discarded once the
relevant behaviour is captured in production code or regression tests.
