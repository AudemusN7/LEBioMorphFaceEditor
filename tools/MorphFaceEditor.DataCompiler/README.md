# MorphFaceEditor DataCompiler

Developer commands for MFE's generated data and corpus checks. The editor consumes the reviewed, embedded outputs; it never runs this tool. Commands use MFE's pinned `lib/LegendaryExplorerCore` dependency and share its readers and policies.

Run commands from the repository root:

```powershell
dotnet run --project tools/MorphFaceEditor.DataCompiler -c Release -- <command> <arguments>
```

| Command and arguments | Purpose / output owner |
|---|---|
| `scan <directory>` | Find canonical morph target sets in PCCs. |
| `compile <package.pcc> <set-name> <output.mft.br>` | Compile a target bundle for Core's `Assets/MorphTargets`. |
| `audit-randomiser <package.pcc>... <report.md> <values.csv> <bundle.mfr.br>` | Audit donors and compile Core's runtime randomisation corpus. |
| `material-metadata <manifest.json> <output.json>` | Refresh reviewed stock material metadata from installed official packages and compiled shader evidence. |
| `unpack-mftr <input.mftr> [output.json]` | Read the MFTR wire format directly, including older/development schemas and manual sidecars. Defaults to JSON beside the input; leaves the MFTR untouched. |
| `texture-registry-audit <LE1\|LE2\|LE3> <output-directory>` | Compare the app's saved registry with effective installed packages; write JSON and TSV reports without changing either input. |
| `verify-texture-registries <registry-directory>` | Check LE1–LE3 texture exclusions, attachment names, duplicate meshes and official donor selection. |
| `build-texture-registries <LE1\|LE2\|LE3\|all> <output-directory>` | Build registry files into the explicitly selected directory. Both arguments are required. |
| `corpus-audit <corpus-directory> <output.json>` | Audit the nine top-level native/cross-game `GlobalMorphs` packages. |
| `corpus-reconciliation <corpus-directory> <output.json>` | Generate the direction-specific asset decisions for LE's `Assets/CrossGameAssetReconciliation.json`. |
| `material-oracle <corpus-directory> <output.json>` | Generate LE's `Assets/MaterialDependencyOracle.json` from native corpora, retaining reviewed Player-only native roles. |
| `conversion-stress <corpus-directory> <output-directory> [seed]` | Run the existing seeded conversion sample. |
| `conversion-full <corpus-directory> <output-directory>` | Run all supported corpus conversions. |
| `conversion-retest <corpus-directory> <prior-results.jsonl> <output-directory>` | Repeat previously failed conversions. |

Ordinary registry publication belongs to the editor's Settings. The developer builder has no default AppData destination. For a separate calibration build:

```powershell
dotnet run --project tools/MorphFaceEditor.DataCompiler -c Release -- build-texture-registries all .codex-temp/texture-registry-calibration
```

The old `conversion-smoke` command is retired. QA owns successful family/game conversion journeys; the regression runner's `conversion` suite retains targeted prerequisite, rejection, exact-donor and texture-transfer contracts. The stress/full/retest runner remains for corpus reconciliation and retained results, with canonical output/reference comparisons in `conversion-full`.

The fixed conversion corpus covers seven morph families per game. The seeded stress sample selects five faces per family (105 originals, 210 directional ports); `conversion-full` selects every eligible fixed-corpus face. Vorcha conversion is retired, including old failed rows supplied to `conversion-retest`; authored Vorcha faces remain loadable. Female Turian placeholder geometry does not establish conversion support. QA separately covers the supported material-only female Batarian routes.

## Generated-artifact ownership

| Embedded output | Authoritative input and producer | Consumer / reviewed exceptions |
|---|---|---|
| Core `Assets/MorphTargets/<game>.<set>.mft.br` | Exact native `MorphTargetSet` and its ordered `Targets`; `compile`. Seven canonical sets per game: HMM, HMF, ASA, SAL, TUR, KRO and BAT. | `MorphTargetCatalog`; profile capability and feature eligibility are runtime policy, not invented target data. `compile` retains the selected source's instanced paths, so equivalent seekfree and level copies need an identity comparison as well as geometry comparison. Vorcha has no target bundle. |
| Core `Assets/Randomisation/GlobalMorphs.mfr.br` | Nine native/directional `GlobalMorphs` PCCs plus reviewed TUF material evidence; `audit-randomiser`. | Core `MorphRandomisationCatalog`; `ReviewedRandomisationExclusions` owns exact exclusions, scope and reason. Compiler deduplication/statistics precede generated human hair-colour aliases. No runtime repair. |
| LE `Assets/StockMaterialMetadata.json` | The accepted JSON itself is the reviewed identity/source manifest; `material-metadata` re-reads those exact GUID/name identities from effective official packages and compiled shaders. | `StockMaterialMetadata` supplies known stock defaults/support; unknown compilations use live inspection. Manifest membership, source paths and optional `compiledDefaults` inclusion remain reviewed inputs. |
| LE `Assets/CrossGameAssetReconciliation.json` | Nine `GlobalMorphs` PCCs; `corpus-reconciliation` compares matching face paths for six directions. | Attachment/texture transfer and texture-registry discovery. Ambiguous evidence is omitted. Two exact accepted LE3 cap aliases to LE1/LE2 remain explicit producer exceptions when some fixtures omit the cap. |
| LE `Assets/MaterialDependencyOracle.json` | Three native `GlobalMorphs` PCCs; `material-oracle` projects their material graphs. | `PccPackageWorkflow` uses class/path import/export roles for missing dependencies; existing exact destination entries take precedence. Producer retains three reviewed LE2 Player imports and seven reviewed LE3 hair exports independently of NPC corpus membership. |

MFTR asset databases are installed/user data, not embedded artifacts. Settings and `build-texture-registries` share `TextureRegistryBuilder`; audits inspect the stored result and never become a second builder. The shader compiler owns build-time shader bundles. QA's frozen timing JSON has its separate exporter and accepted-report provenance under `tools/MorphFaceEditor.QA/TimingBaselines`.

Canonical target-source provenance is verified for all 21 bundles. Original BioWare-supplied inputs are retained locally under ignored `fixtures/local/morph-target-sources/`: `LE1 morphs.pcc` (SHA256 `114A3552F23B3A5B1DFA1AE989516529E1544017385B63F05DEE82AA4BC0EC1C`) and `LE3 morphs.pcc` (`C8597518A9392C812C478C2694D12F0E6EB753169533CA50ECAD4EF034B6960C`). These explain why an installed-package scan cannot recover every original set. Do not replace later-game deltas with earlier-game approximations.

| Bundles | Source and exact-output rule |
|---|---|
| LE1 HMM/HMF/ASA/SAL/KRO/TUR | Compile the corresponding set from supplied LE1. |
| LE1 BAT | Compile supplied LE3 BAT; retain the accepted source-root casing `biog_bat_hed_promorph_r.` instead of `BIOG_BAT_HED_PROMorph_R.`. All native target data and the `PROBase` outer remain unchanged. |
| LE2 ASA/KRO | Compile supplied LE1. |
| LE2 BAT/SAL | Compile supplied LE3; these bundles are already shared byte-for-byte with LE3. |
| LE2 HMM/HMF | Compile installed official `DLC_METR_Patch01/CookedPCConsole/BIOG_HMM_HED_PROMorph.pcc` / `BIOG_HMF_HED_PROMorph_R.pcc`. |
| LE2 TUR | Compile supplied LE1; retain accepted root casing `BIOG_TUR_HED_PROMorph_R.` instead of `biog_tur_hed_promorph_r.`. |
| All seven LE3 bundles | Compile supplied LE3. |

Nineteen outputs reproduce directly; the two explicit prefix-casing projections reproduce the remaining accepted bytes. `compile` preserves source paths by default. Apply these reviewed projections only to `MorphTargetAsset.Source.InstancedPath`, after verifying native target membership/order and exact geometry/normal/bone bits; never infer or rewrite deltas. LE2 shares the supplied LE1 data, with one native HMM membership addition: its 156-target set includes `Jacob`; all 155 common targets match bit-for-bit. Keep the verified LE2 source rather than dropping that target. No accepted dataset was refreshed during this audit.

Generate candidates into a separate directory and compare identity, values and reviewed exceptions before publication:

```powershell
dotnet run --project tools/MorphFaceEditor.DataCompiler -c Release -- corpus-reconciliation "tests/Global Morphs" .codex-temp/generated/CrossGameAssetReconciliation.json
dotnet run --project tools/MorphFaceEditor.DataCompiler -c Release -- material-oracle "tests/Global Morphs" .codex-temp/generated/MaterialDependencyOracle.json
dotnet run --project tools/MorphFaceEditor.DataCompiler -c Release -- material-metadata src/MorphFaceEditor.LegendaryExplorer/Assets/StockMaterialMetadata.json .codex-temp/generated/StockMaterialMetadata.json
```

Current TUF PCCs contain every source game's ported faces grouped beneath `LE1`, `LE2` and `LE3`; they are material placeholders with inherited TUR geometry. The accepted embedded material populations remain 47/33/80. Feeding all 163 faces per game directly into `audit-randomiser` is a population refresh, not reproduction of the accepted corpus. The original input snapshot is unavailable. Native-origin filtering gives 49/34/80 candidates, and current LE2/LE3 resolved material defaults still differ from the accepted evidence, so filtering alone is not an exact reproduction recipe. Review identities, populations and material values before replacement. These placeholders will be replaced when a functional TUF target set exists.

Audit generated data before replacing embedded assets. Keep disposable reports and conversion outputs under `.codex-temp` or `artifacts`; do not commit them. Installed packages remain read-only. Registry audits require current saved registries; rebuild them through Settings after an installation change before interpreting omissions as scanner defects.

Generic package inspection belongs to LECCLI. The retired Research commands map to these existing commands:

| Retired command | LECCLI replacement |
|---|---|
| `locate-export` | `find-object` / `search-objects`, then `find-export` or `list-exports` in the exact package. |
| `inventory-faces` | `list-exports --class BioMorphFace`, `get-properties` and `get-binary`. |
| `actor-inventory` | `list-exports`, `get-properties` and `find-usages` for package-level evidence; MFE's actor-assignment workflow owns eligibility decisions. |
| `trace-material` | `get-properties`, `get-binary`, `resolve-import` and `list-shaders`. |
| `dump-shaders` | `list-shaders` and `export-shader` (`hlsl` or `raw`). |

Consult `LECCLI <command> help` for current options. LECCLI uses its own LegendaryExplorerCore checkout: it provides package evidence, but does not establish how MFE's pinned version behaves. Retain MFE-specific generation and pinned-dependency checks here rather than copying them into a generic inspection tool.
