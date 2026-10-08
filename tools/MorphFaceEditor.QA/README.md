# MFE QA Utility

The QA utility runs production workflows across supported games and archetypes, then saves reports and output packages for review. It exposes failures; it does not repair MFE or game assets.

Double-click `Run_MFE_QA.bat` in the checkout root, or run:

```powershell
dotnet run --project tools/MorphFaceEditor.QA -c Release --
```

Every run chooses a fresh random seed. Cases that select a corpus donor choose a face randomly from the available Global Morphs for that game and archetype. Pass `--seed <integer>` to reproduce the run's choices. Random choices can coincide by chance.

Requires the repository's .NET SDK, corpus PCCs in `tests/Global Morphs` (including TUF), installed LE1/LE2/LE3, current MFE asset databases, and Legendary Explorer's installed UModel build 1589 or newer. Missing prerequisites are reported and do not count as passes. Player and Clipboard workflows use MFE's temporary AppData workspaces. The Windows Clipboard and installed PCCs are untouched.

## Coverage

- `load`, `material-ron`, `clipboard`, `undo-redo` and `benchmark` cover supported game/archetype combinations, including HMM/HMF NPC and Player workflows, Asari, Salarian, male/female Turian, Batarian, Krogan and Vorcha where supported. Player cases start from the installed sex-specific template and create a randomized fixture before testing it.
- `conversion` exercises the full set of supported routes in the shared test conversion matrix. Each route chooses its source face randomly from the matching family in Global Morphs; the report records the selected donor. It keeps PCCs and freshly loads and renders each result.
- `npc-randomisation` runs a 100% production Randomisation for each supported game/NPC archetype, including material-only profiles, and checks persisted geometry and Material values.
- `player-randomisation` covers male and female Players in each game: installed Player → RON → detached RelativeBake workspace → 100% Randomisation → PCC/reload/image.
- `mesh-psk` and `mesh-gltf` randomize HMM morph sliders, save and reopen the bake, then export and import a real mesh into a detached Custom workspace. They check the bake, four Material slots, raw Custom vertex correspondence and inverse-bake recovery.
- `custom-randomisation` creates its own seeded source mesh, assigns compatible Material families to all four HMM slots, then randomizes, exports, reopens and renders. Its inputs remain the same for a recorded seed when running the case alone or within the full matrix.
- `custom-human-materials` explicitly assigns every male and female Human Face/Scalp NPC/Player choice in LE1/LE2/LE3, and checks the Eye/Lash choices and ordering. It checks supported controls, edits, material RON, history and freshly reopened Custom PCCs.
- `custom-human-hair` explicitly covers Hair and Advanced Hair in each game, plus LE3 James Vega Hair. It exercises shared scalp/hair parameters, Custom texture controls, material RON and PCC readback, with 100 production randomisation rolls per route.
- `native-human-hair` exercises the same seven hair graphs on ordinary faces. Hair edits remain BioMaterialOverrides; hair textures remain MIC-owned. Per-parameter checks require the eligible hair colours to participate in the roll batch, rather than accepting a change to an unrelated face colour.
- `cc-scalp-switch` covers LE3 HMM and HMF NPCs selecting ShortAfro and accepting its paired textures and Player scalp graph. It checks one-transaction undo/redo, existing-package save and fresh export, then verifies the selected graph and preserved head data after reopening.
- `player-cc-scalp` checks both official LE3 Player scalp defaults and their persisted CC graphs. `kaidan-preview` renders the native Kaidan graph and verifies its preview-only authoring boundary.
- `pcc-audit` freshly reads every exported PCC and checks structure, references, properties and import resolution against installed donors. Existing copied PCCs retain their structural baseline; newly built outputs have no exemptions. The exact documented LE2 specular-mask import is allowed. Entries under the exact `ME3ExplorerTrashPackage` root are exempt from donor resolution only after their retained data and ancestry are checked. References from retained data into trash entries fail. Custom mesh PCCs must preserve ForcedExport on their `MorphFaceEditor` root and direct SkeletalMesh exports.

Material RON uses capture/write/read/merge, value and history checks, and saved PCC readback. Clipboard uses the production editor copy/paste/Save commands and codec in memory between a face and an independent clone in a copied open PCC. Undo/redo checks edit chronology, whole-roll undo/redo and redo-branch invalidation. TUF checks Material history and preserves authored geometry. Unsupported or incomplete workflows remain failures; the utility does not substitute another edit to make a case pass.

The production Randomisation stage performs 100 head rolls. The report summarizes successful rolls and the average time across all 100; JSON also records total elapsed time and per-roll diagnostics with the roll number, including unresolved or unreadable optional texture diagnostics. It does not list every outcome in the console. The workflow continues from the 100th result so later save, reload and render checks validate the retained outcome, then reports recorded roll failures after the workflow completes.

The Human material cases are included in a full run. To run just the new coverage and audit its outputs:

```powershell
dotnet run --project tools/MorphFaceEditor.QA -c Release -- --case custom-human-materials,custom-human-hair,native-human-hair,cc-scalp-switch,player-cc-scalp,kaidan-preview,pcc-audit
```

These are 25 explicit material cases. Their required material graphs are fixed, even when their source geometry fixture varies with the run seed. Material labels may be translated; case routing uses stable option identities. A missing required graph, parameter or donor is reported rather than substituted with another material.

## Reports and results

Each run writes to a new folder under `artifacts/qa`, with a separate directory for each case. The HTML report summarizes results and links to failures, previews and outputs. The JSON report contains diagnostics, selected donor identities, texture hashes and stage measurements. Artifact filenames include a run token, sequence and original basename; mesh sidecars retain their required names beside the primary mesh, and glTF buffer/image URIs are rewritten when files are renamed. Every case is attempted even if another fails. Exit code 0 means selected cases completed without failures, 1 means cases failed or could not complete, and 2 means invalid invocation or startup failure. Original input package fingerprints are checked after each case, including failures.

Load and Randomisation stages are warned above **1,500 ms**; file transfer, Save and PCC audit stages are warned above **5,000 ms**. Randomisation timing limits apply to the average of the 100 rolls; the total duration is retained in JSON. Performance warnings change an otherwise passing case to the separate **Warning** status; a case with functional failures remains **Failed** and still includes its performance warnings and contributes to performance-warning totals. Render/PNG readback is excluded from timing limits. Player fixture preparation uses one untimed roll and is not part of the production Randomisation average.

Each stage name contains its sequence number, phase and purpose; run and case identity are supplied by the containing report record. `Player fixture preparation` covers stock loading/catalogue construction and fixture export; `Test workflow` covers the randomized-input test. Face/file names and Clipboard copy/paste roles distinguish repeated operations.

Before the matrix tests a game's first face, it records a separate `ColdLoad` warm-up using that game's corpus package. Cold-load timings have no performance limit; subsequent face loads retain the 1,500 ms limit. This measures the first load in the QA session without flushing Windows file caches. The benchmark case likewise records its first load separately from its warm repeats.

Load/export warnings include their phase, package and face. A relinker warning for an unresolved import is informational when a fresh read proves that the output contains exactly one export at the full path, its class matches the canonical oracle, and no matching import exists. The original message and export index remain in JSON evidence. Unverified warnings and duplicate import/export roles remain Warnings. Material RON diagnostics list differing parameters and canonical expected/actual texture identities. Clipboard diagnostics identify the disabled command and dirty/capability state. glTF reporting distinguishes raw Custom geometry checks from inverse-morph sidecar recovery.

The JSON `materialInstanceComparisons` evidence captures each material-instance comparison that detects a stock difference at the actual comparison point, including its source and selected official donor and normalized property differences. These are comparison decisions, not a count of renamed exports: a dependency can be checked more than once, or recursively through a parent MIC. Correlate them with the save warnings by original identity. This diagnostic is enabled only for the QA case and does not alter the material preservation policy.

The unused retained `HMF.Asari` morph is excluded by exact identity from normal corpus donors and native hair fixture seeds. Its pre-existing broken deformation must not become a failure of unrelated material tests; geometry checks remain strict for selected fixtures.

## Commands

```powershell
dotnet run --project tools/MorphFaceEditor.QA -c Release -- --list
dotnet run --project tools/MorphFaceEditor.QA -c Release -- --case custom-randomisation --seed 29092026
dotnet run --project tools/MorphFaceEditor.QA -c Release -- --case player-randomisation
dotnet run --project tools/MorphFaceEditor.QA -c Release -- --case material-ron/le3-hmf-player,undo-redo/le1-tuf
dotnet run --project tools/MorphFaceEditor.QA -c Release -- --output artifacts/qa/my-new-run
dotnet run --project tools/MorphFaceEditor.QA -c Release -- --audit-output artifacts/qa/my-completed-run
dotnet run --project tools/MorphFaceEditor.QA -c Release -- --self-check
```

`--case` accepts a utility group, an exact case ID from `--list`, or a comma-separated selection. `--corpus` selects another corpus directory. Output directories must be new or empty; reruns never overwrite evidence. `--audit-output` audits a completed run and writes a separate report. It leaves previous evidence untouched. Older reports without persisted baseline sets reconstruct them from current original inputs and report that limitation; unresolved imports are never excused by a structural baseline.

The self-check covers timing boundaries, render exclusion, failure propagation, unique stage phases, saved-export warning reconciliation and safe trash exemptions. It also decodes two renamed glTF fixtures after flattening them into one folder; it does not run the game corpus.

Known vanilla exceptions: LE2 `Skin_HumanHED_SpecMulitplier_Mask` may remain unresolved because the Community Patch supplies a fixed import. `HMF_HIR_MIR_LOD0` is an unused broken Miranda mesh; the game uses `HMF_HIR_MIR_MDL`. The runner retains optional preview-catalog warnings about the broken LOD0 and does not substitute or modify MFE's assets.
