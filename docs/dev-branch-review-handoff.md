# Dev Branch Review Handoff

## Final review — 7 September 2026

Implementation and final code review are complete in the root checkout on `dev` (the worktree layout described in PROJECT-CONTEXT is stale). Ryan authorised committing the reviewed changes. No main-branch integration or push is included.

Implemented:

- Shared `PackageIntegrity` ancestor preflight for skeletal meshes, external textures and cross-game texture embedding. Import collisions abort; no import promotion is attempted. Dependency package ancestors are reserved before relinking, including ordinary morph export.
- Shared hierarchy, duplicate identity, property/binary/header/stack/component reference validation on reopened temporary packages in both writer services. Existing conversion-specific HMM alias and unknown-property checks remain.
- `MaterialisationVerifier` compares retained property/binary slots against the source-to-destination relink map, including detection of valid-but-wrong destination UIndices. Required failures and unclassified relinker reports are fatal. The pinned LEC cross-game property-pruning notice is admitted only for a verified associated entry; pre-materialisation optional omissions remain unchanged.
- Structured per-root diagnostics saved by the conversion stress runner to `materialisations.jsonl`, with final package verification status.
- Eleven focused regressions in `PackageIntegrityTests.cs`. Collision/relink save failures assert byte-identical originals and successful reopening; other cases cover wrong references, retained binary failure reports, invalid hierarchies, preservation of pre-existing package issues, cyclic parent chains, ancestor reuse, same-game skeletal meshes, PROShort01/optional donors, and all twelve LE2/LE3-to-LE1 Add/Tat policy cases.

Evidence and important corrections:

- Completed verification: 293/293 release harness tests and 220/220 supported conversions passed, with no skipped conversions. All 350 recorded materialised roots passed reference verification.
- Final review tightened issue identities to include table indices, preventing a new duplicate from inheriting an old baseline exemption. Parent cycles are rejected before path formatting or duplicate detection can recurse. The three integrity regressions and five context-operation regressions passed after these changes; the full harness/corpus results above precede these narrowly scoped review fixes.
- The stress runner now returns a failing exit code when any conversion fails, matching the retest runner.
- The reviewed catalogue deliberately omits LE1 PROShort01 in LE3; LE2 retains a donor. This was tested without changing the catalogue. Existing LE1 stock Add donors remain preferred over embedding.
- The preliminary 230-port audit (`artifacts/integrity-audit-before`) completed with 220 passes and ten failures, all unsupported Vorcha-to-LE1 attempts (five from each later game). The stress runner now excludes those impossible routes, including retests, and expects 220 supported attempts.
- Six apparent package-integrity failures came from import-parent structures already present in corpus PCCs. Existing-package transactions now capture a structural baseline and reject only newly introduced issues; new conversion/export packages remain strictly clean. A focused regression proves this boundary.
- Two tests added for temporary files (`LE3 VorchaMorphs.pcc` and root-level TUF PCCs) were removed completely. The Gibbed regression now selects its LE2 human-female source by profile identity instead of the obsolete `Human Female.*` path convention.

Completed verification logs:

- Launcher: `artifacts/run-integrity-verification.ps1` (hidden PowerShell process; PID in `artifacts/integrity-verification-corrected/runner.pid`).
- Status: `artifacts/integrity-verification-corrected/status.json` (`IsComplete` and per-stage exit codes).
- Full release harness: `artifacts/integrity-verification-corrected/all-tests.log`.
- Final 220-port supported-route stress run: `artifacts/integrity-verification-corrected/conversion-stress.log` and `corpus/{progress.json,results.jsonl,materialisations.jsonl}` in that directory.
- Runner errors: `runner-error.log` / `runner.stderr.log` in that directory, if present.

The user-owned untracked `docs/old_outdated/` directory was untouched. The original review below is retained as historical context; its absolute clean-package rule is superseded for existing packages by the verified baseline policy above. New packages and newly materialised assets still require clean hierarchies and coherent references.

**Review range:** `d657491e6d9920767a978792373b03808302b450..c30f72f32f90c8c91c65f8f36c200ab59af7c005`

**Purpose:** Preserve the timed review findings and the agreed implementation direction for a fresh agent.

## Domain invariants

- Actor material assignment may target supported actors in the current PCC's `PersistentLevel` and supported local archetypes.
- Imported/core PROMorph MICs must never be patched; only package-local MIC exports are writable.
- A MIC shared through a local archetype is intentionally shared state. Changing all inheriting actors is expected.
- Materialised cross-package assets should use export-backed hierarchies. Exports are preferred over imports.
- All six directed LE1/LE2/LE3 conversion routes must continue working.
- Optional assets may be deliberately omitted when no valid target donor exists.
- Selected LE2/LE3 Add/Tat textures intentionally become package-stored `Texture2D` exports when converting to LE1.
- A transaction must never install a malformed package. Atomic replacement prevents torn writes, not semantic corruption.

## Finding 1: shared actor MIC mutation

**Disposition:** Withdrawn as a defect; optional UX clarification only.

The original review observed that `ActorAssignmentInventoryService.Materials.cs:53` accepts compatible local MICs without rejecting MICs referenced by multiple actors. That would be wrong for per-instance isolation, but it matches this feature's intended archetype semantics.

The existing safety boundary rejects imported materials and materials resolved outside the workspace PCC. Candidate discovery is limited to supported actor/archetype families. Therefore the tool does not directly patch MICs in the core PROMorph package.

Do not automatically clone an archetype-shared MIC. An optional future UI improvement could label a material as actor-owned or archetype-owned and explain that an archetype edit affects all inheritors. This is not a merge blocker.

## Finding 2: import/export ancestor collision

**Severity:** Important; confirmed package-integrity risk.

`ExternalSkeletalMeshMaterializer.EnsurePackagePath` uses `FindExport` for each package ancestor. If an ancestor already exists as an `ImportEntry`, it can create an export with the same path, producing ambiguous identities or an invalid hierarchy.

New-conversion staging runs export-under-import and duplicate-identity checks. Ordinary `MorphFacePackageWriter.SaveExisting` attachment materialisation does not apply equivalent validation before replacement.

### Required rule

> Any asset selected for materialisation must have an entirely export-backed package hierarchy. Never create an export alongside an import with the same identity.

### Implementation plan

1. Add a synthetic failing test containing an imported package root and a donor export requested beneath that root.
2. Preflight every ancestor with `FindEntry(currentPath, "Package")`.
3. Reuse an existing `ExportEntry`; create a new export only when no entry exists.
4. If an `ImportEntry` occupies the identity, either perform a separately proven promotion/relink or abort the transaction with a precise error.
5. Use abort-on-collision as the safe first implementation. It closes the corruption path without pretending that general import promotion is solved for every game/direction.
6. Extract shared structural verification for all writer paths that can add exports:
   - no export has an import parent;
   - `EntryChecker.CheckForDuplicateIndices` returns no issues;
   - retained destination references resolve;
   - existing invalid-HMM-alias checks remain where relevant.
7. Capture the original destination bytes in the regression test and prove that collision failure leaves them byte-identical and reopenable.

Likely files:

- `src/MorphFaceEditor.LegendaryExplorer/ExternalSkeletalMeshMaterializer.cs`
- `src/MorphFaceEditor.LegendaryExplorer/MorphFacePackageWriter.cs`
- `src/MorphFaceEditor.LegendaryExplorer/MorphFacePackageContextService.cs`
- `tests/MorphFaceEditor.Tests/PackageContextTests.cs`

Focused verification:

```powershell
dotnet run --project tests\MorphFaceEditor.Tests -c Release -- --suite package "import ancestor"
dotnet run --project tests\MorphFaceEditor.Tests -c Release -- --suite package
```

## Finding 3: relinker reports are unconditionally downgraded

**Severity:** Important, but blanket rejection of every report is incorrect.

At `ExternalSkeletalMeshMaterializer.cs:123`, every `RelinkerOptionsPackage.RelinkReport` entry becomes a warning and materialisation continues. LegendaryExplorerCore uses the report for genuine failures such as `Relink failed...` and `binary relinking failed...`, as well as outcomes that may be tolerable. Root class/path checks alone do not prove the retained dependency graph is valid.

### Behaviour that must remain valid

- LE1 PROShort01 hair converted to LE2/LE3 when unavailable target material dependencies are intentionally omitted.
- Missing or ambiguous optional attachment donors.
- Unsupported texture overrides falling back to destination material defaults.
- LE2/LE3 Add/Tat textures embedded as package-stored LE1 `Texture2D` exports.

### Required rule

> Optional assets may be deliberately omitted, but an asset that the transfer engine elects to materialise must emerge internally coherent.

### Classification boundary

- Decisions made before materialisation—catalogue `null`, no unique donor, unsupported parameter, destination default—remain domain warnings.
- Once materialisation starts, failure affecting the root asset, a retained dependency, or a required property/binary reference is fatal.
- A failed optional dependency is acceptable only after its reference is actively removed or replaced with a deliberate fallback. Never leave the donor package's original UIndex in destination data.
- Do not classify solely through guessed message substrings. Use the report's associated entry plus the final reference state.

### Implementation plan

1. Instrument the conversion stress runner to record, per materialised root:
   - source/target game and asset kind;
   - root path;
   - report entry path/class and message;
   - whether the dependency was retained, removed, replaced, or unresolved;
   - whether final package verification succeeded.
2. Run representative corpus cases before enforcing policy:
   - PROShort01 from LE1 to LE2 and LE3;
   - every LE2/LE3 to LE1 Add/Tat allowlist case;
   - a missing optional attachment donor;
   - ordinary same-game skeletal-mesh materialisation.
3. Introduce structured diagnostics, for example `MaterialisationIssueSeverity.Warning` and `.Fatal`, associated with the root and affected entry.
4. Keep intentional pre-materialisation omissions outside the materialiser failure path.
5. After relinking, validate every retained non-zero property/binary reference. It must resolve inside the destination to the intended class/path. The root `ObjectBinary` must parse successfully.
6. Throw before save/replacement for a fatal root or retained-dependency issue.
7. When an optional dependency is omitted, explicitly remove/zero its reference and emit a domain warning.
8. Add failure-injection coverage proving that a required relink failure leaves the original PCC byte-identical.

Likely files:

- `src/MorphFaceEditor.LegendaryExplorer/ExternalSkeletalMeshMaterializer.cs`
- `src/MorphFaceEditor.LegendaryExplorer/MorphFaceAttachmentTransferEngine.cs`
- `src/MorphFaceEditor.LegendaryExplorer/MorphFaceTextureTransferEngine.cs`
- `tools/MorphFaceEditor.DataCompiler/ConversionStressRunner.cs`
- `tests/MorphFaceEditor.Tests/PackageContextTests.cs`

Focused verification:

```powershell
dotnet run --project tests\MorphFaceEditor.Tests -c Release -- --suite package "relink"
dotnet run --project tests\MorphFaceEditor.Tests -c Release -- --suite package "package-stored"
```

## Recommended execution order

1. Implement the import-collision regression and abort-on-collision guard.
2. Centralise structural verification across conversion and ordinary save paths.
3. Collect real `RelinkReport` evidence from the corpus.
4. Encode the evidence-backed warning/fatal classification.
5. Enforce retained dependency-graph integrity.
6. Run all six conversion directions and the complete test harness.

Each stage should be independently reviewed and committed. Do not combine import promotion with the initial collision guard unless corpus tests prove it safe.

## Final acceptance criteria

- No operation creates duplicate import/export identities.
- No export is parented beneath an import.
- Import collisions fail safely unless a verified promotion path exists.
- Optional omissions continue producing warnings rather than failed conversions.
- Package-stored LE1 Add/Tat texture fallback continues working.
- Every retained materialised reference resolves to the intended destination entry.
- Required relink/binary failures abort before replacement.
- Every rejected mutation leaves the original PCC byte-identical and reopenable.
- Actor MIC behaviour remains unchanged: local archetype sharing is preserved and imported PROMorph MICs remain read-only.
- All package, materials, UI, and full-suite tests pass.
- The conversion corpus passes LE1→LE2, LE1→LE3, LE2→LE1, LE2→LE3, LE3→LE1, and LE3→LE2.

## Review limitations to remember

The timed review could not compile or execute tests because NuGet failed before compilation with `NU1301` TLS/authentication errors reaching `https://api.nuget.org/v3/index.json`. Re-run restore/build once connectivity works.

The 5,879-line reconciliation catalogue was structurally inspected but not independently validated row-by-row.
