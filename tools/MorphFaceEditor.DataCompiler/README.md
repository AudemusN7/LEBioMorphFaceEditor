# MorphFaceEditor DataCompiler

DataCompiler generates and checks data used by MFE. It uses MFE's pinned `lib/LegendaryExplorerCore`; the editor reads the embedded results and doesn't run the compiler itself.

Run commands from the repository root:
```powershell
dotnet run --project tools/MorphFaceEditor.DataCompiler -c Release -- <command> <arguments>
```
Replace the example paths and set name below with your own. `scan` can find target sets in a folder of PCCs; `compile` writes a bundle from one selected set.
```powershell
dotnet run --project tools/MorphFaceEditor.DataCompiler -c Release -- scan "C:\MyPackages"
dotnet run --project tools/MorphFaceEditor.DataCompiler -c Release -- compile "C:\MyPackages\Head.pcc" "Package.PROBase.HMM_BaseMorphSet" "C:\MyOutput\head.mft.br"
dotnet run --project tools/MorphFaceEditor.DataCompiler -c Release -- build-texture-registries all "C:\MyOutput\Registries"
```
## Commands

| Command | What it does |
| --- | --- |
| `scan <directory>` | Find canonical morph target sets in PCCs. |
| `compile <package.pcc> <set-name> <output.mft.br>` | Compile a morph target bundle. |
| `audit-randomiser <package.pcc>... <report.md> <values.csv> <bundle.mfr.br>` | Audit donors and compile a randomisation corpus. |
| `material-metadata <manifest.json> <output.json>` | Refresh reviewed stock material metadata from official packages and compiled shaders. |
| `unpack-mftr <input.mftr> [output.json]` | Read an MFTR directly, including older schemas and manual sidecars. The default output is JSON beside the input; the MFTR isn't changed. |
| `texture-registry-audit <LE1\|LE2\|LE3> <output-directory>` | Compare a saved registry with effective installed packages and write JSON and TSV reports. |
| `verify-texture-registries <registry-directory>` | Check LE1–LE3 exclusions, attachment names, duplicate meshes and official donor selection. |
| `build-texture-registries <LE1\|LE2\|LE3\|all> <output-directory>` | Build registries into the specified directory. Both arguments are required. |
| `corpus-audit <corpus-directory> <output.json>` | Audit the GlobalMorphs and dedicated TUF packages. |
| `corpus-reconciliation <corpus-directory> <output.json>` | Generate direction-specific asset decisions, omitting ambiguous evidence. |
| `material-oracle <corpus-directory> <output.json>` | Generate material dependency roles, retaining reviewed existing and Player-only roles. |
| `conversion-stress <corpus-directory> <output-directory> [seed]` | Run a seeded sample of corpus conversions. |
| `conversion-full <corpus-directory> <output-directory>` | Run all supported corpus conversions with canonical output/reference comparisons. |
| `conversion-retest <corpus-directory> <prior-results.jsonl> <output-directory>` | Repeat previously failed conversions. |

For normal use, build registries through the editor's Settings. Registry audits read saved registries and installed packages without changing either. Rebuild through Settings after an installation change before treating omissions as scanner defects.

## Generated data

The embedded outputs include morph target bundles and the randomisation corpus in `src/MorphFaceEditor.Core/Assets`, plus stock material metadata, cross-game reconciliation and the material dependency oracle in `src/MorphFaceEditor.LegendaryExplorer/Assets`. MFTR databases are installed/user data, not embedded assets.

The source packages used to produce the embedded data aren't included in this repository. Supplying other PCCs can produce useful candidates, but doesn't necessarily reproduce the existing assets. Write candidates separately and compare identities, values and reviewed exceptions before replacing anything. `compile` preserves source paths, so compare identity as well as geometry when using equivalent seekfree and level copies. Don't substitute earlier-game deltas for later-game data.

`material-metadata` uses the accepted JSON as its manifest, reading its exact GUID/name identities from effective official packages and compiled shaders. Manifest membership, source paths and optional `compiledDefaults` are reviewed inputs.

## Corpus layout

The corpus commands require a separately supplied collection of packages. `corpus-audit` expects nine top-level PCCs whose names contain `GlobalMorphs`, plus `TUF/LE1 TUF.pcc`, `TUF/LE2 TUF.pcc` and `TUF/LE3 TUF.pcc`. Reconciliation and conversion use the named native packages `LE1 GlobalMorphs.pcc`, `LE2 GlobalMorphs.pcc` and `LE3 GlobalMorphs.pcc`, and directional packages such as `LE1 to LE2 GlobalMorphs.pcc`. The material oracle uses the three native packages and three TUF packages.

Conversion stress tests select five faces per family; the full run selects every eligible face. TUF comparisons use the target game's dedicated package. Vorcha conversion isn't supported.

## Inspect packages

Use LECCLI for general package inspection, with `LECCLI <command> help` for options. It uses its own LegendaryExplorerCore checkout, so check MFE's pinned version when investigating behaviour specific to MFE.
