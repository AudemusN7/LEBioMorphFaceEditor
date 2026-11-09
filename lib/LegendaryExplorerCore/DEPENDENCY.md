# LegendaryExplorerCore

MFE uses [LegendaryExplorerCore 6.5.0.0, commit 804db0b](https://github.com/ME3Tweaks/LegendaryExplorer/commit/804db0b9a37d6184e8a06e4fecb30270d2fb48c8), built for **.NET 10 / win-x64 / WinRelease** with a portable PDB.

`TexConverter.dll` is built separately from that same commit's `LegendaryExplorer/TexConverter/TexConverter.vcxproj`, using **WinRelease / x64**, MSVC **14.44.35207** (v143) and Windows SDK **10.0.26100.0**. This replaces the upstream prebuilt DLL, which imports Visual C++ Debug runtimes. The Release project statically links the C/C++ runtime; its remaining non-Windows dependency is the redistributable OpenMP runtime `VCOMP140.DLL`.

To rebuild, extract that commit's TexConverter directory and run Visual Studio MSBuild (with the C++ desktop workload installed):

```powershell
MSBuild.exe TexConverter.vcxproj /t:Rebuild /p:Configuration=WinRelease /p:Platform=x64 /p:VCToolsVersion=14.44.35207
```

Use `dumpbin /dependents TexConverter.dll` to inspect the result before updating the pinned hash. Run `dotnet run --project tools/MorphFaceEditor.Tests -c Release -- --suite tooling NativeDependencyTests` to check the bundled output for Debug runtime imports and verify native texture conversion. A successful conversion on a developer machine alone does not establish redistributability.

## SHA-256

```text
D0417B39CE27BBA575EEC6FB529DFB0614F14750636F0242C0AA7B00CBA7B745  CompressionWrappers.dll
0E96E67D4D09C7EAABE48FDD3D8B42E659956A9F50B42A759219EE22AE204048  ISACTTools.dll
5BEFC63667C5B14E1605E8DBB6183ED9C784FCBC9F15B0F2F1B64E97ED6089B0  LegendaryExplorerCore.dll
67A202181CFD9EA01DE317D932BF0601DEE58BA141D184B8BD341E7DF4F3B0F6  LegendaryExplorerCore.pdb
D7FDDC6F0B5A6F2E3D6D2C125B6ABB06B526B6EDEBA8275D5AE91BBED6902F8F  TexConverter.dll
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
