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
| `audit-randomiser <package.pcc>... <report.md> <values.csv> <bundle.mfr.br>` | Audit donors and compile the app's randomisation corpus. |
| `reconstruct-vorcha <le2.pcc> <le3.pcc> <le2-output.mft.br> <le3-output.mft.br>` | Reconstruct the reviewed Vorcha target bundles. |
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
