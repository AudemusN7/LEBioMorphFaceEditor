# LegendaryExplorerCore

MFE uses [LegendaryExplorerCore 6.5.0.0, commit 804db0b](https://github.com/ME3Tweaks/LegendaryExplorer/commit/804db0b9a37d6184e8a06e4fecb30270d2fb48c8), built for **.NET 10 / win-x64 / WinRelease** with a portable PDB.

## SHA-256

```text
D0417B39CE27BBA575EEC6FB529DFB0614F14750636F0242C0AA7B00CBA7B745  CompressionWrappers.dll
0E96E67D4D09C7EAABE48FDD3D8B42E659956A9F50B42A759219EE22AE204048  ISACTTools.dll
5BEFC63667C5B14E1605E8DBB6183ED9C784FCBC9F15B0F2F1B64E97ED6089B0  LegendaryExplorerCore.dll
67A202181CFD9EA01DE317D932BF0601DEE58BA141D184B8BD341E7DF4F3B0F6  LegendaryExplorerCore.pdb
CE8D9728246585F0389F50699B3BA37F3D3FECE722E4770B346583CAECBC5607  TexConverter.dll
```

## Check the files

Builds check the Core, CompressionWrappers and TexConverter hashes; Debug also checks the PDB. Run that check separately with:

```powershell
pwsh -File eng/Verify-LegendaryExplorerCore.ps1
```

Add `-Configuration Debug` for the PDB. MFE doesn't use or ship ISACTTools. Other versions are pinned in `Directory.Packages.props` and project lock files.

## ImageSharp licence

ImageSharp **3.2.0** uses `sixlabors.lic` at the repository root. This build-only Community licence covers `MorphFaceEditor.LegendaryExplorer` until **22 January 2028**.

Override it with `SixLaborsLicenseFile` or `SixLaborsLicenseKey`. [Assembly-scoped Community keys can be shared with contributors](https://sixlabors.com/posts/security-support-and-license-enforcement/); keep unrestricted keys private.
