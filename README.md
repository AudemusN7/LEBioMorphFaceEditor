# LE BioMorphFace Editor

WPF editor for Mas Effect Legendary Edition `BioMorphFace` assets. Current support: Human Male/Female, Asari, Salarian, male Turian, Krogan and Batarian across LE1-LE3.

Features: morph/final-skeleton/material/texture/attachment editing, semantic undo, transactional saves, cross-game conversion, TSE RON import/export, legacy Gibbed import, and PSK/glTF/MD5 interchange through UModel. Preview uses D3D11 and is an approximation of UE3 rendering.

```powershell
dotnet run --project src/MorphFaceEditor -c Debug
dotnet build MorphFaceEditor.slnx -c Release

# Fast default suite; opt into affected integration areas.
dotnet run --project tests/MorphFaceEditor.Tests -c Release
dotnet run --project tests/MorphFaceEditor.Tests -c Release -- --suite ui
dotnet run --project tests/MorphFaceEditor.Tests -c Release -- --suite rendering
dotnet run --project tests/MorphFaceEditor.Tests -c Release -- --suite materials
dotnet run --project tests/MorphFaceEditor.Tests -c Release -- --suite package
dotnet run --project tests/MorphFaceEditor.Tests -c Release -- --suite tooling
dotnet run --project tests/MorphFaceEditor.Tests -c Release -- --suite all
```

Mesh exports include a versioned `.mfe.json` sidecar. Keep it beside the mesh for a lossless round trip; mesh-only import cannot reconstruct metadata-only controls, bone edits or unique lower-LOD branches.

The application embeds canonical compressed morph targets and the randomisation donor corpus. Runtime editing never depends on the GlobalMorphs packages. Data regeneration remains available through `tools/MorphFaceEditor.DataCompiler`.