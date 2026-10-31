# MFE QA Utility

The QA utility runs MFE workflows across the supported games and archetypes, then saves reports, previews and output packages for inspection. It reports problems; it doesn't repair the editor or game assets.

## Before you start

You'll need the repository's .NET SDK, installed LE1, LE2 and LE3, current MFE asset databases, and Legendary Explorer's installed UModel build 1589 or newer. The Global Morphs corpus needed for this utility belongs in `fixtures/Global Morphs`, including TUF and the three `BAF/BAF LE1.pcc`, `BAF/BAF LE2.pcc` and `BAF/BAF LE3.pcc` files. These files can be downloaded from the mod page as game assets are not included in this repo.

Player and Clipboard cases use MFE's temporary AppData workspaces. The utility doesn't change the Windows Clipboard or installed PCCs; it checks input package fingerprints after each case, including failures. Its diagnostics go into the QA reports rather than the editor's persistent AppData log.

## Run and select cases

Use the following command:
```powershell
dotnet run --project tools/MorphFaceEditor.QA -c Release --
```
The normal run includes every workflow group except `benchmark` and `donor-sweep`. Use `--list` to see the available groups and exact case IDs. For example:
```powershe
dotnet run --project tools/MorphFaceEditor.QA -c Release -- --list
dotnet run --project tools/MorphFaceEditor.QA -c Release -- --case material-ron/le3-hmf-player,undo-redo/le1-tuf
dotnet run --project tools/MorphFaceEditor.QA -c Release -- --case BAF,pcc-audit
dotnet run --project tools/MorphFaceEditor.QA -c Release -- --case custom-randomisation --seed 29092026
dotnet run --project tools/MorphFaceEditor.QA -c Release -- --exhaustive
dotnet run --project tools/MorphFaceEditor.QA -c Release -- --case donor-sweep,pcc-audit
dotnet run --project tools/MorphFaceEditor.QA -c Release -- --case benchmark
```
`--case` accepts a group, an exact case ID, an archetype such as `BAF`, or a comma-separated selection. Include `pcc-audit` when you want to audit outputs from a filtered run. Benchmark cases must be selected explicitly; an archetype filter won't enable them.

Each run chooses a random seed and may sample a Global Morphs donor. Pass the recorded `--seed <integer>` to reproduce those choices. `--exhaustive` adds load, export and reopen checks for every eligible donor, but edit combinations are still sampled. BAF donor selection requires the female Batarian mesh profile.

Other options:
```powershell
dotnet run --project tools/MorphFaceEditor.QA -c Release -- --corpus "path/to/corpus"
dotnet run --project tools/MorphFaceEditor.QA -c Release -- --output artifacts/qa/my-new-run
dotnet run --project tools/MorphFaceEditor.QA -c Release -- --audit-output artifacts/qa/my-completed-run
dotnet run --project tools/MorphFaceEditor.QA -c Release -- --self-check
```
An output directory must be new or empty. `--audit-output` audits a completed run into a separate report without changing its evidence. Older reports without saved structural baselines reconstruct them from the current original inputs and report that limitation. `--self-check` checks the utility's own reporting and fixture handling; it doesn't run the game corpus.

## What the cases cover

| Group | Coverage |
| --- | --- |
| `load`, `material-ron`, `clipboard`, `undo-redo` | Load supported NPC and Player profiles; check material preset round trips, copy/paste, saves and edit history. Unsupported commands and unsafe morph pastes must be rejected without changing the target. |
| `ron-full`, `ron-morph`, `ron-cross-game` | Export and import headmorphs or morph presets, save and reopen them, and compare the persisted result. Cross-game cases use the explicit import route. Disabled capabilities are checked too. |
| `ron-missing-assets`, `tse-ron` | Check missing attachment and texture handling, plus the external TSE RON fixtures and their PCC round trips. Missing assets must be reported and omitted; the current strict accessory rejection is a known failing gap, not a pass. |
| `pcsav`, `pcsav-extra` | Exercise curated careers, default heads, randomisation, save/reopen and full-head replacement. Checks include geometry, feature and bone data, ordering and unrelated career bytes; incomplete native LOD data must be refused safely. |
| `conversion` | Convert supported NPC families between games, then load and render the exported PCC. ALN keeps its authored bakes and has no morph conversion route. |
| `npc-randomisation`, `player-randomisation`, `custom-randomisation` | Run 100-roll stress batches for the relevant NPC, Player or Custom workflows, then check saved results. Other cases use smaller samples or deliberate edits. |
| `mesh-psk`, `mesh-gltf` | Edit and reopen a known editable face, then export and import a mesh into Custom, checking geometry, materials and inverse-bake recovery. |
| `custom-human-materials`, `custom-human-hair`, `native-human-hair` | Exercise Human material and hair choices, controls, randomisation, presets and PCC readback. Required graphs and donors must be present; they aren't substituted. |
| `cc-scalp-switch`, `player-cc-scalp`, `kaidan-preview` | Check LE3 scalp switching and Player defaults, history and persistence, plus Kaidan's native preview boundary. |
| `pcc-audit` | Freshly read exported PCCs and check structure, references, properties and import resolution against installed donors. |
| `donor-sweep`, `benchmark` | Check each eligible donor's load/export/reopen, or measure a first load and three warm repeats. Benchmark is load-only. |

Player RON outputs and newly written PCSAV references must also pass the production runtime-source validator; a structurally valid PCC alone doesn't prove those references are safe. Unavailable native career references may remain untouched during unrelated edits. Full replacement must refuse an unsafe head.

PCC audit allows for `Skin_HumanHED_SpecMulitplier_Mask` import to remain broken in LE2, as this is fixed by the Unofficial LE2 Patch. Existing copied packages retain their structural baseline; new outputs have no baseline exemptions, and unresolved imports aren't excused by one. Trash entries are exempt from donor resolution only after their data and ancestry are checked; retained data must not reference them. The optional preview catalogue can warn about the unused broken `HMF_HIR_MIR_LOD0`; since the game uses `HMF_HIR_MIR_MDL`.

## Reports and timing

Each run creates a folder under `artifacts/qa`, with a folder per case. Open the HTML report for results, slow stages, previews and outputs; JSON contains the detailed diagnostics, donor identities, texture hashes and measurements. Every selected case is attempted. Exit code **0** means no failures, **1** means a case failed or couldn't complete, and **2** means an invalid invocation or startup failure.

A deformation mismatch on load is informational: an authored bake can differ from reconstructed targets. Structural topology checks and comparisons of saved geometry, features, bones and materials remain strict. A relinker warning is informational only when a fresh read verifies one matching export of the correct class and no matching import; otherwise it remains a warning.

Timing caps come from the accepted baseline in `TimingBaselines`. Fixed inputs have 25% headroom and variable inputs 50%, with at least 100 ms slack, rounded up to 100 ms. A stage above its cap warns; above **2×** the cap for fixed inputs or **3×** for variable inputs it fails the case. Unmapped Load/Randomisation stages use 1,500 ms and FileTransfer stages 5,000 ms, with failure above 3×. ColdLoad, Render and Diagnostic stages have no cap. Randomisation is judged by average time per actual roll, not batch total.

Run timing workloads serially without competing workloads. ColdLoad records the first load without clearing Windows caches; compare first-use and session catalogue stages like for like. Some diagnostic timings overlap, so don't add them together. Managed allocation is process-wide allocation during a stage, not peak memory or proof of a leak; working set is a process snapshot.

The baseline is frozen: a slow run cannot raise its own limits. Recalibrate only from another reviewed, accepted dataset. The baseline export script uses the pinned original audit and writes a separate corrected audit; existing reports keep their original evidence.
