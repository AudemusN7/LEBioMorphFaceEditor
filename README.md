# LE BioMorphFace Editor

WPF editor for Mas Effect Legendary Edition `BioMorphFace` assets. Current support: Human Male/Female, Asari, Salarian, male Turian, Krogan and Batarian across LE1-LE3.

Features: morph/final-skeleton/material/texture/attachment editing, semantic undo, transactional saves, cross-game conversion, TSE RON import/export, legacy Gibbed import, and PSK/glTF interchange through UModel. Preview uses D3D11 and is an approximation of UE3 rendering.

```powershell
dotnet run --project src/MorphFaceEditor -c Debug
dotnet build MorphFaceEditor.slnx -c Release

# Fast default suite; opt into affected integration areas.
dotnet run --project tools/MorphFaceEditor.Tests -c Release
dotnet run --project tools/MorphFaceEditor.Tests -c Release --no-build -- --list
dotnet run --project tools/MorphFaceEditor.Tests -c Release --no-build -- --suite ui UiWorkspaceTests
dotnet run --project tools/MorphFaceEditor.Tests -c Release --no-build -- --suite all
```

See [regression test ownership and suites](tools/MorphFaceEditor.Tests/README.md) for focused checks and the [QA harness](tools/MorphFaceEditor.QA/README.md) for broad workflow matrices. Generic PCC inspection uses LECCLI.

Mesh exports include a versioned `.mfe.json` sidecar. Keep it beside the mesh for a lossless round trip; mesh-only import cannot reconstruct metadata-only controls, bone edits or unique lower-LOD branches.

The application embeds canonical compressed morph targets and the randomisation donor corpus. Runtime editing never depends on the GlobalMorphs packages. Data regeneration remains available through `tools/MorphFaceEditor.DataCompiler`.

Developer commands and data ownership are documented in [DataCompiler](tools/MorphFaceEditor.DataCompiler/README.md). For example, unpack an MFTR to JSON beside the input, or pass an optional JSON output path:

```powershell
dotnet run --project tools/MorphFaceEditor.DataCompiler -c Release -- unpack-mftr "C:\path\to\LE1.mftr"
```

## Logging

Daily logs live in `%LocalAppData%\LE BioMorphFace Editor\Logs`. Each session starts with `SESSION N`; records use `yyyy-MM-dd HH:mm:ss.fff zzz [INF|WAR|ERR]` followed by compact event fields. Session numbering restarts in each daily file, and a running editor rolls over at midnight.

Launch `MorphFaceEditor.exe -verboselogging` for workspace refresh, face loading, registry lifecycle, feature preview/drag and optional material-member diagnostics. Ordinary logging keeps operation results, randomisation summaries, warnings and errors. In-process QA observers still receive verbose diagnostics, while the QA runner suppresses all AppData log writes.

Normal randomisation uses `RAND:NOR <tag>.<morph> <game> STR<morph>/<material> SEED<morph>[/<material>]`; `-` marks an absent seed. Cursed randomisation uses `RAND:CUR` and its single shared seed.

Startup removes application logs last written more than seven days ago and orphaned `*.workspace.pcc` files. Live workspace leases protect files owned by other editor instances; unleased files from older builds are preserved while another editor is running. Diagnostic ZIPs include today and the preceding two calendar days of logs, plus the current workspace and databases.
