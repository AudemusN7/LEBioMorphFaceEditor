# MorphFaceEditor shader research tool

This console keeps reverse-engineering code out of the production reader and renderer. It is intentionally a developer tool, not a shipped application dependency.

## Pass-1 commands

Unpack one MFTR file into a readable JSON file, without an installed game or
registry rebuild:

```powershell
dotnet run --project tools/MorphFaceEditor.Research -c Release -- unpack-mftr "C:\path\to\LE1.mftr"
```

This writes `LE1.json` beside the input. Pass an explicit output path as a
second argument if preferred: `unpack-mftr "C:\path\to\LE1.mftr" "C:\path\to\report.json"`.
The command reads the MFTR header and compressed JSON directly, so it can also
unpack an older schema that the current editor can no longer load. It never
changes the MFTR input or requires the user's game files. The JSON includes
every indexed texture and attachment mesh, their occurrences, and the chosen
effective occurrence.

Custom additions are stored separately per game as `LE1.manual.mftr`,
`LE2.manual.mftr`, or `LE3.manual.mftr` beside the installed database. Pass a
manual file to the same `unpack-mftr` command to inspect its selected assets and
occurrences. A failed relink also writes `<game>.manual.missing.log` beside it,
listing the original PCC paths and exports to restore or select again.

Audit an existing installed texture registry without rebuilding it:

```powershell
dotnet run --project tools/MorphFaceEditor.Research -c Release -- texture-registry-audit LE1 .codex-temp/texture-registry-audit
```

Repeat with `LE2` and `LE3`. For each game the command writes a readable
`*-registry.json` copy of the complete registry, a compact `*-summary.json`,
and tab-separated `*-included.tsv`, `*-missing-paths.tsv`, `*-omitted.tsv`,
and `*-hir-meshes.tsv` tables. Start with `missing-paths` in a spreadsheet:
it has one row per texture path absent from the registry, with an example
package. `included` lists every stored occurrence and identifies the effective
one. `omitted` lists every current installed Texture2D export absent from the
saved registry, including duplicate paths and possible intentional exclusions.
Its path-rule column
shows only the public name filter; character-creator and reviewed-reconciliation
exceptions are also part of the scanner. `hir-meshes` lists installed
SkeletalMesh exports whose object name contains `HIR`. The command does not change the registry or
installed packages. If the game installation changed since the registry was
built, rebuild it first through the editor settings before interpreting
omissions as discovery-rule gaps.

To inspect a rebuilt registry without replacing the editor's installed MFTRs,
pass an output directory to the builder:

```powershell
dotnet run --project tools/MorphFaceEditor.Research -c Release -- build-texture-registries LE1 .codex-temp/texture-registry-calibration
```

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

Regenerate the runtime material dependency oracle from the native LE1, LE2,
and LE3 corpora:

```powershell
dotnet run --project tools/MorphFaceEditor.Research -c Release -- material-oracle "tests/Global Morphs" "src/MorphFaceEditor.LegendaryExplorer/Assets/MaterialDependencyOracle.json"
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
