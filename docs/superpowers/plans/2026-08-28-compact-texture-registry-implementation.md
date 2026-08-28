# Compact Texture Registry Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the full ObjectInstanceDB pipeline with a compact per-game texture registry that builds visibly in one pass, merges installed and working-package textures in searchable relevance-ranked pickers, and lets material randomisation resolve its complete installed donor pool.

**Architecture:** Core owns detached texture, mip, registry, search, and availability models. The LegendaryExplorer adapter scans each effective PCC once, persists a Brotli-compressed versioned registry atomically, and loads that small registry without reopening game packages. WPF merges the registry with every local `Texture2D`, keeps local paths authoritative, and uses the same merged path resolver for manual selection and coherent randomisation families.

**Tech Stack:** .NET 9, C# 13, WPF, LegendaryExplorerCore, `System.Text.Json`, `System.IO.Compression.BrotliStream`, the repository's custom console test runner.

**Spec:** `docs/superpowers/specs/2026-08-28-compact-texture-registry-design.md`

## Global Constraints

- Work only on the existing `dev` worktree at `C:\Users\ryana\Documents\LE BioMorphFace Editor\.worktrees\dev`.
- Normal package and face loading must never scan the installed game or await registry construction.
- The builder opens each effective installed package no more than once per explicit build.
- Only exact non-default `Texture2D` exports matching `PROMorph`, `HMM_HIR`, `HMF_HIR`, or a supported shared-eye fragment enter the installed registry.
- Every `Texture2D` in the working PCC remains selectable even if it does not match registry discovery rules.
- Working-package exports suppress exact case-insensitive installed-path duplicates in the picker.
- Registry replacement is atomic; cancellation or failure preserves the previous verified file byte-for-byte.
- Randomisation retains coherent GlobalMorphs-derived texture families and resolves them through the merged local/installed catalogue.
- External selections remain preview-only and keep the existing save/export guard until path-preserving materialisation is implemented.
- Do not read, replace, or delete Legendary Explorer's shared ObjectInstanceDB files or previously generated MFE `.bin` files.

---

### Task 1: Detached Registry Models, Discovery, Ranking, and Availability

**Files:**
- Modify: `src/MorphFaceEditor.Core/Materials/TextureCatalogModels.cs`
- Create: `src/MorphFaceEditor.Core/Materials/TextureRegistryModels.cs`
- Modify: `tests/MorphFaceEditor.Tests/TextureCatalogTests.cs`

**Interfaces:**
- Consumes: existing `TextureCatalogGame`, `TextureCatalogProfile`, and `TextureCatalogCandidate` call sites.
- Produces: `TextureMipStorageRecord`, `TextureRegistrySnapshot`, `TextureRegistryDiscovery.IsRelevantPath(string)`, `TextureCatalogAvailability`, and deterministic candidate construction/ranking used by persistence, scanning, picker merge, and randomisation.

- [ ] **Step 1: Write failing core tests**

Add cases proving all supported discovery fragments are case-insensitive, unrelated paths are rejected, mip metadata remains attached to occurrences, duplicate occurrences choose highest mount priority, and availability resolves full paths before unique object-name fallbacks:

```csharp
new("texture registry: discovery admits morph HIR and shared-eye paths", DiscoveryAdmitsSupportedPaths),
new("texture registry: availability resolves installed and local paths", AvailabilityResolvesMergedPaths),
new("texture registry: ambiguous object names are not resolved", AmbiguousObjectNamesAreRejected)
```

Use concrete fixtures such as `BIOG_SAL_HED_PROMorph_R.Add.SAL_HED_PRO_Add1`, `BIOG_HMM_HIR_PRO.Hair.HMM_HIR_Diff`, `BIOG_ASA_EYE.Materials.ASA_EYE_Diff`, and `EngineResources.WhiteSquareTexture`.

- [ ] **Step 2: Run the focused tests and confirm the new cases fail**

Run:

```powershell
dotnet run --project tests/MorphFaceEditor.Tests -c Release -- --suite materials "texture registry"
```

Expected: the new tests fail because the registry snapshot, discovery, mip, and availability types do not exist.

- [ ] **Step 3: Add the detached models and shared discovery rules**

Define these exact records in `TextureRegistryModels.cs`:

```csharp
public sealed record TextureMipStorageRecord(
    int Index, int Width, int Height, int StorageType,
    int UncompressedSize, int CompressedSize, int ExternalOffset,
    string? TextureCacheName);

public sealed record TextureRegistrySnapshot(
    int SchemaVersion,
    TextureCatalogGame Game,
    DateTimeOffset BuiltAtUtc,
    int InstalledPackageCount,
    IReadOnlyList<TextureCatalogCandidate> Candidates)
{
    public const int CurrentSchemaVersion = 1;
}
```

Add `IReadOnlyList<TextureMipStorageRecord> Mips` to `TextureCatalogOccurrence`, retain `HasExternalMips` as detached metadata set by the LEC scanner, and centralise discovery in:

```csharp
public static class TextureRegistryDiscovery
{
    public static bool IsRelevantPath(string path) =>
        BaseFragments.Any(fragment => path.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}
```

The exact fragment set is `PROMorph`, `HMM_HIR`, `HMF_HIR`, `HMM_EYE`, `HMF_EYE`, `HED_EYE`, `ASA_EYE`, `SAL_EYE`, `TUR_EYE`, `KRO_EYE`, and `BAT_EYE`.

- [ ] **Step 4: Add deterministic merged-path availability**

Implement:

```csharp
public sealed class TextureCatalogAvailability
{
    public TextureCatalogAvailability(
        IEnumerable<string> localPaths,
        IEnumerable<TextureCatalogCandidate> installedCandidates);

    public bool TryResolve(string requestedPath, out string canonicalPath);
    public bool Contains(string requestedPath);
}
```

Exact full-path matches win. An object-name-only request resolves only when exactly one merged candidate has that object name; ambiguity returns `false`. Keep candidate ordering deterministic by full path.

- [ ] **Step 5: Run the material suite**

Run:

```powershell
dotnet run --project tests/MorphFaceEditor.Tests -c Release -- --suite materials
```

Expected: all material tests pass after updating occurrence fixtures with mip arrays.

- [ ] **Step 6: Commit the core boundary**

```powershell
git add src/MorphFaceEditor.Core/Materials tests/MorphFaceEditor.Tests/TextureCatalogTests.cs
git commit -m "feat: define compact texture registry models"
```

---

### Task 2: Versioned Brotli Registry Store and Status Provider

**Files:**
- Create: `src/MorphFaceEditor.LegendaryExplorer/TextureRegistry/TextureRegistryContracts.cs`
- Create: `src/MorphFaceEditor.LegendaryExplorer/TextureRegistry/TextureRegistryPaths.cs`
- Create: `src/MorphFaceEditor.LegendaryExplorer/TextureRegistry/TextureRegistryStore.cs`
- Replace tests in: `tests/MorphFaceEditor.Tests/ObjectDatabaseTests.cs`
- Modify: `tests/MorphFaceEditor.Tests/MaterialTests.cs`

**Interfaces:**
- Consumes: `TextureRegistrySnapshot` from Task 1.
- Produces: `TextureRegistryState`, `TextureRegistryStatus`, `TextureRegistryPaths.GetPath(MorphFaceGame)`, `TextureRegistryStore.GetStatus`, `Read`, and `WriteAtomic`.

- [ ] **Step 1: Replace OIDB persistence tests with failing compact-store tests**

Cover round-trip preservation of every snapshot field and mip field, rejection of bad magic/schema/wrong game/truncated payload, successful atomic replacement, and cancellation before replacement preserving original bytes.

Use this file contract in fixtures:

```text
uint32 magic = 0x5254464D  // ASCII MFTR in little endian
int32 schemaVersion
int32 game
int64 uncompressedJsonLength
remaining bytes = Brotli-compressed UTF-8 JSON
```

- [ ] **Step 2: Run the focused persistence tests and confirm failure**

Run:

```powershell
dotnet run --project tests/MorphFaceEditor.Tests -c Release -- --suite materials "texture registry store"
```

Expected: failure because `TextureRegistryStore` and its contracts do not exist.

- [ ] **Step 3: Implement paths and status contracts**

Define:

```csharp
public enum TextureRegistryState { Missing, Ready, Building, Outdated, Cancelled, Failed }

public sealed record TextureRegistryStatus(
    MorphFaceGame Game, TextureRegistryState State, string? FilePath,
    long? FileSize, DateTimeOffset? LastBuilt, int? TextureCount,
    string? ErrorMessage);
```

`TextureRegistryPaths.CreateDefault()` must target `%LOCALAPPDATA%\BioMorphFaceEditor\TextureRegistries` and return `LE1.mftr`, `LE2.mftr`, or `LE3.mftr`.

- [ ] **Step 4: Implement strict read and atomic write**

`TextureRegistryStore.Read(game)` validates the complete header, caps the declared JSON size at 256 MiB, deserializes with `System.Text.Json`, checks schema/game/non-null required collections, and returns a detached snapshot. `GetStatus` distinguishes an unsupported well-formed schema (`Outdated`, amber) from missing or malformed files (red). `WriteAtomic(snapshot, cancellationToken)` writes `<target>.tmp`, flushes to disk, reopens through `ReadFile`, checks equality-critical metadata, checks cancellation, then uses `File.Move(temp, target, overwrite: true)`.

On all exceptions, delete only the exact adjacent `.tmp` file. Never touch an existing `.mftr` before verification succeeds.

- [ ] **Step 5: Run persistence and material tests**

Run:

```powershell
dotnet run --project tests/MorphFaceEditor.Tests -c Release -- --suite materials
```

Expected: all tests pass, including malformed input and byte-preservation cases.

- [ ] **Step 6: Commit the persistence boundary**

```powershell
git add src/MorphFaceEditor.LegendaryExplorer/TextureRegistry tests/MorphFaceEditor.Tests/ObjectDatabaseTests.cs tests/MorphFaceEditor.Tests/MaterialTests.cs
git commit -m "feat: persist compact texture registries"
```

---

### Task 3: Single-Pass Installed-Package Builder

**Files:**
- Create: `src/MorphFaceEditor.LegendaryExplorer/TextureRegistry/TextureRegistryPackageScanner.cs`
- Create: `src/MorphFaceEditor.LegendaryExplorer/TextureRegistry/TextureRegistryBuilder.cs`
- Modify: `tests/MorphFaceEditor.Tests/ObjectDatabaseTests.cs`

**Interfaces:**
- Consumes: `TextureRegistryDiscovery`, `TextureRegistrySnapshot`, `TextureRegistryStore`, `MELoadedFiles`, `MEPackageHandler`, `Texture2D`, and `MELoadedDLC`.
- Produces: `TextureRegistryBuildPhase`, `TextureRegistryBuildProgress`, `ITextureRegistryPackageScanner`, `TextureRegistryBuilder.RebuildAsync`, and `RebuildAllAsync`.

- [ ] **Step 1: Add failing builder tests**

Create fake package-scan results and prove:

```csharp
new("texture registry builder: scans each package once", ScansEachPackageOnce),
new("texture registry builder: groups paths by mount precedence", GroupsPathsByMountPrecedence),
new("texture registry builder: reports scan write verify phases", ReportsEveryBuildPhase),
new("texture registry builder: cancelled rebuild preserves active file", CancelledBuildPreservesActiveFile),
new("texture registry builder: rebuild all is sequential", RebuildAllIsSequential)
```

The fake scanner records every supplied package path. Assert each effective path has a count of one and LE1/LE2/LE3 complete in that order.

- [ ] **Step 2: Run focused builder tests and confirm failure**

Run:

```powershell
dotnet run --project tests/MorphFaceEditor.Tests -c Release -- --suite materials "texture registry builder"
```

Expected: failure because the bespoke scanner and builder are absent.

- [ ] **Step 3: Implement one-open package scanning**

Define the test seam:

```csharp
internal interface ITextureRegistryPackageScanner
{
    IReadOnlyList<TextureRegistryScannedTexture> Scan(
        MorphFaceGame game, string packagePath, CancellationToken cancellationToken);
}

internal sealed record TextureRegistryScannedTexture(
    string InstancedPath, TextureCatalogOccurrence Occurrence);
```

The LEC implementation opens the package once with `MEPackageHandler.OpenMEPackage(packagePath, forceLoadFromDisk: true)`, iterates `Exports`, requires `!IsDefaultObject` and `ClassName == "Texture2D"` case-insensitively, applies `TextureRegistryDiscovery.IsRelevantPath`, and constructs one detached occurrence. Capture each non-empty mip's `index`, dimensions, numeric `storageType`, compressed/uncompressed sizes, `externalOffset`, and `TextureCacheName`.

- [ ] **Step 4: Implement build orchestration and mount grouping**

Define phases and progress exactly:

```csharp
public enum TextureRegistryBuildPhase { ScanningPackages, WritingRegistry, VerifyingRegistry, Ready }

public sealed record TextureRegistryBuildProgress(
    MorphFaceGame Game, TextureRegistryBuildPhase Phase,
    int PackagesProcessed, int TotalPackages,
    string? CurrentPackageName, int TextureCount);
```

`RebuildAsync` obtains `MELoadedFiles.GetFilesLoadedInGame(game).Values`, scans sequentially on a worker thread, groups by case-insensitive instanced path, orders occurrences by descending mount priority then origin then package path, creates the snapshot, writes through `TextureRegistryStore.WriteAtomic`, rereads it, and reports `Ready`. Check cancellation before each package, before write, and before replacement.

- [ ] **Step 5: Run builder tests and a Release build**

Run:

```powershell
dotnet run --project tests/MorphFaceEditor.Tests -c Release -- --suite materials "texture registry"
dotnet build MorphFaceEditor.slnx -c Release
```

Expected: all registry tests pass and the solution builds with zero warnings and errors.

- [ ] **Step 6: Commit the single-pass builder**

```powershell
git add src/MorphFaceEditor.LegendaryExplorer/TextureRegistry tests/MorphFaceEditor.Tests/ObjectDatabaseTests.cs
git commit -m "feat: build texture registries in one pass"
```

---

### Task 4: Registry Management Window and Visible Progress

**Files:**
- Create: `src/MorphFaceEditor/ViewModels/TextureRegistrySettingsViewModel.cs`
- Create: `src/MorphFaceEditor/Views/TextureRegistrySettingsWindow.xaml`
- Create: `src/MorphFaceEditor/Views/TextureRegistrySettingsWindow.xaml.cs`
- Modify: `src/MorphFaceEditor/Services/WpfEditorDialogService.cs`
- Modify: `src/MorphFaceEditor/App.xaml.cs`
- Replace tests in: `tests/MorphFaceEditor.Tests/ObjectDatabaseSettingsTests.cs`
- Modify: `tests/MorphFaceEditor.Tests/Program.cs`
- Delete: `src/MorphFaceEditor/ViewModels/ObjectDatabaseSettingsViewModel.cs`
- Delete: `src/MorphFaceEditor/Views/ObjectDatabaseSettingsWindow.xaml`
- Delete: `src/MorphFaceEditor/Views/ObjectDatabaseSettingsWindow.xaml.cs`

**Interfaces:**
- Consumes: `TextureRegistryStore`, `TextureRegistryBuilder`, and build progress from Task 3.
- Produces: the existing settings-button workflow backed only by MFE registries, with the approved dark UI and build/cancel visibility rules.

- [ ] **Step 1: Rewrite settings tests against registry state**

Assert each row exposes only `GameLabel`, `SourceLabel == "MorphFace Editor"`, traffic-light `StatusLabel`/`StatusColour`, and `LastBuiltLabel`. Assert progress labels for all phases, the active button becomes Cancel, other individual buttons and Rebuild All hide during an individual build, and only Rebuild All remains as Cancel during an all-games build.

- [ ] **Step 2: Run the focused UI tests and confirm failure**

Run:

```powershell
dotnet run --project tests/MorphFaceEditor.Tests -c Release -- --suite ui "texture registry settings"
```

Expected: failure because the renamed view model and phase-aware labels are absent.

- [ ] **Step 3: Implement registry settings state**

Map phases to labels exactly:

```csharp
ScanningPackages => $"Scanning packages — {PackagesProcessed:N0} / {TotalPackages:N0}",
WritingRegistry => "Writing compact registry",
VerifyingRegistry => "Verifying registry",
Ready => $"Ready — {TextureCount:N0} textures"
```

Keep the current reliable cancellation pattern: one owned `CancellationTokenSource`, no overlapping builds, previous Ready registry preserved after cancellation/failure, and cache invalidation only after a successful verified replacement.

- [ ] **Step 4: Replace the window while preserving its approved visual layout**

Retain the custom dark title bar, 560-pixel width, 500-pixel height, full-width progress bar aligned with the game cards, padding below progress, and traffic-light cards. Change title/copy to Texture Registries and explain that builds scan installed packages once for face, hair, scalp, and eye textures; remove all ObjectInstanceDB/schema/file-size wording.

- [ ] **Step 5: Rewire startup and dialog ownership**

Construct `TextureRegistryPaths`, `TextureRegistryStore`, `TextureRegistryBuilder`, and `TextureRegistrySettingsViewModel` in `App.OnStartup`. The dialog service opens `TextureRegistrySettingsWindow`; no `ObjectDatabaseProvider` or `ObjectDatabaseBuilder` is constructed.

- [ ] **Step 6: Run UI and material suites**

Run:

```powershell
dotnet run --project tests/MorphFaceEditor.Tests -c Release -- --suite ui
dotnet run --project tests/MorphFaceEditor.Tests -c Release -- --suite materials
```

Expected: settings interaction and registry persistence/build tests all pass.

- [ ] **Step 7: Commit the management workflow**

```powershell
git add src/MorphFaceEditor tests/MorphFaceEditor.Tests
git commit -m "feat: manage compact texture registries"
```

---

### Task 5: Fast Runtime Loading and Relevance-Ranked Local/Installed Merge

**Files:**
- Replace: `src/MorphFaceEditor.LegendaryExplorer/TextureRegistry/TextureCatalogService.cs`
- Modify: `src/MorphFaceEditor/Services/PackageReferenceService.cs`
- Modify: `src/MorphFaceEditor/ViewModels/MainWindowViewModel.cs`
- Modify: `src/MorphFaceEditor/ViewModels/FaceEditorViewModel.cs`
- Modify: `src/MorphFaceEditor/ViewModels/MaterialEditorViewModel.cs`
- Modify: `src/MorphFaceEditor/ViewModels/MaterialTextureEditorViewModel.cs`
- Modify: `tests/MorphFaceEditor.Tests/TextureCatalogTests.cs`
- Modify: `tests/MorphFaceEditor.Tests/UiSmokeTests.cs`

**Interfaces:**
- Consumes: compact store and candidates from Tasks 1-3 plus `PackageReferenceCatalog.Textures`.
- Produces: `TextureCatalogService.ReadAsync(game, cancellationToken)`, merged picker options, local-path precedence, and no installed-package work during face loading.

- [ ] **Step 1: Add failing runtime and picker tests**

Prove that a ready registry read calls only the compact store, a missing registry returns immediately, all working-package textures remain when the registry is missing, irrelevant local textures remain searchable, an exact-path local texture suppresses the external option, distinct external paths remain, and ranking is active → preferred → shared → other local → other installed.

- [ ] **Step 2: Run focused catalogue tests and confirm failure**

Run:

```powershell
dotnet run --project tests/MorphFaceEditor.Tests -c Release -- --suite materials "texture catalogue"
```

Expected: local merge and new rank assertions fail against the current registry-replaces-local implementation.

- [ ] **Step 3: Replace OIDB projection with compact-file loading**

`TextureCatalogService.ReadAsync` loads `TextureRegistryStore.Read(game)` on a worker thread, caches by `(game, file length, last-write UTC)`, and returns all stored candidates. It accepts no profile because discovery is complete at build time. `Invalidate(game)` removes only that game's cached snapshot.

- [ ] **Step 4: Implement the picker merge**

In `MaterialTextureEditorViewModel`, always create options for every supplied `PackageAssetListItem`. Add installed options only when no local option has the same instanced path case-insensitively. Search local options by display name, full path, package filename, and `Open package`; search installed options through `TextureCatalogSearch`. Assign one rank tuple so the combined filtered list sorts as specified, followed by object name and full path.

`HasExternalRegistrySelection` remains true only for an installed option. `ResolveInstancedPathAsync` uses exact merged-path resolution first and refuses ambiguous object-name matches.

- [ ] **Step 5: Keep face creation immediate**

Construct `FaceEditorViewModel` immediately with package-local textures and registry unavailable. Rename `PopulateTextureRegistryAsync` to `LoadTextureRegistryAsync`; it may only deserialize/cache the `.mftr` file and update the still-current editor. Remove all “indexing started/completed” wording from logs because package scanning exists only in Settings.

- [ ] **Step 6: Run catalogue, UI, and package suites**

Run:

```powershell
dotnet run --project tests/MorphFaceEditor.Tests -c Release -- --suite materials "texture catalogue"
dotnet run --project tests/MorphFaceEditor.Tests -c Release -- --suite ui
dotnet run --project tests/MorphFaceEditor.Tests -c Release -- --suite package
```

Expected: all pass, with no test-observable installed-package enumeration during face load.

- [ ] **Step 7: Commit runtime loading and merge**

```powershell
git add src/MorphFaceEditor.LegendaryExplorer/TextureRegistry/TextureCatalogService.cs src/MorphFaceEditor tests/MorphFaceEditor.Tests
git commit -m "feat: merge local and installed texture choices"
```

---

### Task 6: Installed-Pool Material Randomisation

**Files:**
- Modify: `src/MorphFaceEditor/Services/MorphRandomisationCatalog.cs`
- Modify: `src/MorphFaceEditor.Core/Randomisation/MaterialRandomiser.cs`
- Modify: `src/MorphFaceEditor/ViewModels/FaceEditorViewModel.cs`
- Modify: `src/MorphFaceEditor/ViewModels/MaterialEditorViewModel.cs`
- Modify: `src/MorphFaceEditor/ViewModels/MaterialTextureEditorViewModel.cs`
- Modify: `tests/MorphFaceEditor.Tests/RandomisationTests.cs`
- Modify: `tests/MorphFaceEditor.Tests/MaterialEditingTests.cs`

**Interfaces:**
- Consumes: `TextureCatalogAvailability`, merged picker resolution, existing donor `MaterialTextureFamilies`, and `MaterialRandomiser.CreateProposal`.
- Produces: availability-filtered compatible donors/families and bounded retry across installed coherent families.

- [ ] **Step 1: Add failing installed-pool randomisation tests**

Create donors whose texture families are: fully local, fully installed, missing one required member, missing only an optional member, and unreadable at decode time. Assert installed families are eligible, required-incomplete families are excluded, optional-incomplete families apply supported members, and one unreadable chosen family causes another eligible family to be attempted without changing numeric material proposals twice.

- [ ] **Step 2: Run focused randomisation tests and confirm failure**

Run:

```powershell
dotnet run --project tests/MorphFaceEditor.Tests -c Release -- --suite core "installed texture"
dotnet run --project tests/MorphFaceEditor.Tests -c Release -- --suite materials "randomisation texture"
```

Expected: installed paths are skipped or a failed family ends the texture proposal.

- [ ] **Step 3: Expose merged availability from material editors**

Add:

```csharp
public bool CanResolveInstancedPath(string instancedPath);
public IReadOnlySet<string> ResolvableTexturePaths { get; }
```

to `MaterialTextureEditorViewModel`, and aggregate per-parameter availability in `MaterialEditorViewModel`. Required members use the existing `IsRequiredRandomisationTextureMember` rules; optional members do not invalidate a family.

- [ ] **Step 4: Filter and retry coherent donor families**

Add to `MorphRandomisationCatalog`:

```csharp
public IReadOnlyList<MorphRandomisationDonor> ProjectInstalledMaterialDonors(
    string profileKey,
    Func<string, string, bool> canResolveParameterPath);
```

Return donor copies whose `MaterialTextureFamilies` contain only complete applicable families; preserve each donor's numeric material evidence. Add an optional `IReadOnlySet<string>? excludedTextureSignatures` parameter to `MaterialRandomiser.CreateProposal` and remove matching signatures before family selection. `PreparedMaterialRandomisation` returns `FailedTextureFamilySignatures` for required-member decode failures. `FaceEditorViewModel.RandomiseStandardAsync` reruns proposal preparation with accumulated exclusions and the same random seed, bounded by the eligible family-signature count. This keeps scalar/vector proposals stable and applies numeric values and textures once inside the existing single undo aggregate.

- [ ] **Step 5: Run core and material suites**

Run:

```powershell
dotnet run --project tests/MorphFaceEditor.Tests -c Release -- --suite core
dotnet run --project tests/MorphFaceEditor.Tests -c Release -- --suite materials
```

Expected: existing stable-seed and family-coherence tests still pass, and installed external paths now resolve through the registry.

- [ ] **Step 6: Commit randomisation integration**

```powershell
git add src/MorphFaceEditor/Services/MorphRandomisationCatalog.cs src/MorphFaceEditor/ViewModels tests/MorphFaceEditor.Tests
git commit -m "feat: randomise from installed texture families"
```

---

### Task 7: Remove OIDB Pipeline, Verify, and Prepare the UI Checkpoint

**Files:**
- Delete: `src/MorphFaceEditor.LegendaryExplorer/TextureRegistry/ObjectDatabaseBuilder.cs`
- Delete: `src/MorphFaceEditor.LegendaryExplorer/TextureRegistry/ObjectDatabaseContracts.cs`
- Delete: `src/MorphFaceEditor.LegendaryExplorer/TextureRegistry/ObjectDatabasePaths.cs`
- Delete: `src/MorphFaceEditor.LegendaryExplorer/TextureRegistry/ObjectDatabaseProvider.cs`
- Delete: `src/MorphFaceEditor.LegendaryExplorer/TextureRegistry/TextureCatalogProjector.cs`
- Modify: `docs/superpowers/specs/2026-08-28-compact-texture-registry-design.md`
- Modify: `docs/TEXTURE-REGISTRY-DESIGN.md`

**Interfaces:**
- Consumes: all compact-registry replacements and tests from Tasks 1-6.
- Produces: one discovery/persistence pipeline, clean build/test results, and an LE1 manual verification checklist.

- [ ] **Step 1: Prove no production OIDB references remain**

Run:

```powershell
rg -n "ObjectInstanceDB|ObjectDatabaseProvider|ObjectDatabaseBuilder|TextureCatalogProjector" src
```

Expected: no matches in production source. References in historical docs may remain explicitly labelled retired.

- [ ] **Step 2: Delete obsolete production files and update documentation**

Remove the five OIDB source files listed above. Mark the compact design status as implemented through runtime/randomisation and make the older root design document point readers to the superseding approved spec.

- [ ] **Step 3: Run every deterministic suite**

Run:

```powershell
dotnet run --project tests/MorphFaceEditor.Tests -c Release -- --suite all
dotnet build MorphFaceEditor.slnx -c Release
```

Expected: every test passes; build completes with zero warnings and zero errors.

- [ ] **Step 4: Inspect the final diff for scope and accidental generated files**

Run:

```powershell
git status --short
git diff --stat
git diff --check
```

Expected: only source, tests, and design/plan documents are changed; `git diff --check` emits no output.

- [ ] **Step 5: Commit the cleanup**

```powershell
git add src tests docs
git commit -m "refactor: retire object database texture indexing"
```

- [ ] **Step 6: Hand off one meaningful UI and real-data checkpoint**

Ask Ryan to build LE1 from Texture Registries, then verify:

1. progress visibly moves through scan, write, verify, and ready;
2. cancelling a rebuild preserves the previous Ready registry;
3. opening an LE1 package and face is immediately interactive;
4. every package-local `Texture2D` remains searchable;
5. installed LE1 face/eye choices appear above unrelated local/installed options by relevance;
6. selecting an external texture updates live preview; and
7. material randomisation can select a texture family absent from the working PCC.

Record build duration, package count, texture count, and `.mftr` size from the visible status/log. This is the first point in the plan that requires hands-on UI testing.
