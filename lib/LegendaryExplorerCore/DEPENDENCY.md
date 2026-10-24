# LegendaryExplorerCore dependency

- Source: `https://github.com/ME3Tweaks/LegendaryExplorer`
- Commit: `804db0b9a37d6184e8a06e4fecb30270d2fb48c8`
- Assembly version: `6.5.0.0`
- Target: `.NET 10`, `win-x64`, `WinRelease`
- Symbols: portable PDB

## SHA-256

```text
D0417B39CE27BBA575EEC6FB529DFB0614F14750636F0242C0AA7B00CBA7B745  CompressionWrappers.dll
0E96E67D4D09C7EAABE48FDD3D8B42E659956A9F50B42A759219EE22AE204048  ISACTTools.dll
5BEFC63667C5B14E1605E8DBB6183ED9C784FCBC9F15B0F2F1B64E97ED6089B0  LegendaryExplorerCore.dll
67A202181CFD9EA01DE317D932BF0601DEE58BA141D184B8BD341E7DF4F3B0F6  LegendaryExplorerCore.pdb
CE8D9728246585F0389F50699B3BA37F3D3FECE722E4770B346583CAECBC5607  TexConverter.dll
```

Managed transitive dependency versions are pinned centrally in `Directory.Packages.props` and captured in project lock files. The LegendaryExplorer adapter build verifies the Core DLL and both shipped native DLLs before resolving references; Debug also verifies the copied portable PDB. `eng/Verify-LegendaryExplorerCore.ps1 [-Configuration Debug]` invokes that same build target. ISACTTools is retained from the source snapshot but is unused and is not shipped by MFE.

The adapter pins ImageSharp 3.2.0 for the v3 security fixes. Its direct-package build validation uses the repository-root `sixlabors.lic` by default; override `SixLaborsLicenseFile` for another supplied file, or use the package-supported `SixLaborsLicenseKey` property. The included assembly-scoped Community licence covers `MorphFaceEditor.LegendaryExplorer` until 22 January 2028. Six Labors permits [sharing assembly-scoped Community keys with contributors](https://sixlabors.com/posts/security-support-and-license-enforcement/); unrestricted keys must remain private. The licence file is a build input and is not shipped with MFE.
