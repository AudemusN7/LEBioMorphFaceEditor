# Compact Texture Registry Design

**Status:** Implemented through compact build, runtime picker merge, and installed-pool randomisation on the `dev` branch on 28 August 2026. Path-preserving save-time materialisation remains the next delivery stage.

## Purpose

Replace the full ObjectInstanceDB-based texture-discovery pipeline with a bespoke, compact, per-game MorphFace Editor texture registry. Building and indexing become one visible package scan. Normal face loading never performs registry construction or projection.

This revision supersedes the OIDB provider, MFE-owned full OIDB builder, and runtime compact-catalogue projection described in `docs/TEXTURE-REGISTRY-DESIGN.md`. The texture identity, profile ranking, live-preview, and path-preserving save requirements from that document remain applicable.

## User-visible behaviour

The existing **Texture Databases…** button continues to open the management window, but the window describes **Texture Registries** rather than Object Databases.

Each LE1, LE2, and LE3 block shows only:

- source: MorphFace Editor;
- status: Missing, Building, Ready, Outdated, Cancelled, or Failed; and
- last-built time.

Traffic-light colours retain their current meanings: Ready is green; Building, Outdated, and Cancelled are amber; Missing, malformed, and Failed are red. Each game has a **Build** or **Rebuild** action, and the window retains **Rebuild All**.

During a build the same window remains the progress authority. It shows the current game and phase:

1. `Scanning packages — 2,418 / 6,594`
2. `Writing compact registry`
3. `Verifying registry`
4. `Ready — 1,026 textures`

Rebuild All processes LE1, LE2, and LE3 sequentially. Only the active action becomes **Cancel**; unrelated build actions remain hidden, matching the approved UI behaviour.

If a registry is missing, ordinary morph loading and editing remain available immediately. Texture controls show that installed choices are unavailable and direct the user to **Texture Databases…**. The editor does not silently begin a long scan when opening a package or face.

Once a registry is ready, face loading deserializes the compact file and populates the texture picker without a game-package indexing phase. Loading or switching a face must not be gated on registry construction.

## Single-pass bespoke builder

For the selected game, the builder obtains the effective installed package list from `MELoadedFiles.GetFilesLoadedInGame`. It opens every effective package at most once during that build.

While each package is open, the builder:

1. enumerates non-default exports;
2. retains only exact `Texture2D` exports whose instanced paths match the registry discovery rules;
3. extracts all metadata required for display, preview, collision analysis, TFC dependency reporting, and later path-preserving porting; and
4. appends a detached occurrence record to the in-progress registry.

There is no full intermediate OIDB and no second projection pass. “Building” and “indexing” are the same package traversal.

Cancellation is checked between package scans. The builder accumulates data in memory, writes only after the scan completes, verifies the newly written file, and atomically replaces the previous active registry. Cancellation or failure preserves the previous registry byte-for-byte.

Builds remain explicitly user-controlled. Installing or removing mods does not trigger automatic work; the user rebuilds the affected game from the management window.

## Discovery rules

An export is admitted only when its exact class is `Texture2D` and its full instanced path matches at least one of:

- `PROMorph`, case-insensitively;
- `HMM_HIR` or `HMF_HIR`, for Human hair/scalp and SpecShift assets; or
- a shared-eye path fragment declared by a supported morph profile.

Profile rules are discovery supplements and ranking signals, never exclusionary base-game whitelists. Installed mod exports matching the same rules are first-class candidates.

Paths are grouped by game plus exact case-insensitive instanced path. Multiple installed occurrences remain attached to one UE3-facing candidate. The effective occurrence is selected using the installed game’s mount precedence, not package enumeration order.

## Compact registry contents

Each registry stores:

- format magic, schema version, and game;
- build timestamp and installed package count;
- exact texture instanced path and object name;
- source package path and export UIndex;
- dimensions, pixel format, texture group, and TFC name;
- external-mip state and offsets/storage flags required for later verification;
- display origin and base-game/official-DLC/mod provenance;
- mount priority; and
- every verified occurrence required for collision analysis.

The on-disk format is an MFE-owned, versioned Brotli-compressed JSON payload with a small binary header. This keeps the format inspectable and deterministic without adding a serialization dependency. Readers reject unknown versions, wrong games, malformed payloads, and incomplete required fields.

Default locations are:

```text
%LOCALAPPDATA%\BioMorphFaceEditor\TextureRegistries\LE1.mftr
%LOCALAPPDATA%\BioMorphFaceEditor\TextureRegistries\LE2.mftr
%LOCALAPPDATA%\BioMorphFaceEditor\TextureRegistries\LE3.mftr
```

Previously generated MFE OIDB `.bin` files and Legendary Explorer’s shared OIDBs are left untouched but are no longer read by the texture-selection workflow.

## Runtime catalogue and picker

The compact registry is the runtime catalogue; there is no additional indexing stage. It is loaded asynchronously and cached in memory per game, but deserialization must not block construction of the face editor.

Every texture picker is built from a merged catalogue:

- every `Texture2D` already present in the currently loaded working package, whether or not it matches an installed-registry discovery rule; and
- the installed game's compact registry candidates for face, scalp, hair, and eye textures.

An exact case-insensitive instanced-path match is shown once. The working-package export is authoritative for that path because it is already available to save without external materialisation. Local and installed occurrences with different paths remain distinct. A missing registry therefore removes only installed-game choices; it never removes the working package's own textures.

Search matches object name, full instanced path, source package, and display origin case-insensitively. Ordering is:

1. the currently authored/effective texture;
2. exact active profile/family matches;
3. shared profile assets, especially eyes;
4. other textures from the working package; and
5. all other verified morph-related textures from the installed registry.

The picker continues to preserve local and external source identities separately. Selecting an external candidate previews the exact mounted source without mutating the working PCC. Until path-preserving save materialisation is implemented, save and export remain explicitly blocked when an external registry choice is active.

## Randomisation integration

The existing GlobalMorphs-derived randomisation corpus remains the semantic authority for coherent texture families. It knows which diffuse, normal, specular, mask, and eye members belong together; the compact registry supplies the installed source occurrence for each member. Randomisation must resolve family paths against the same merged catalogue used by manual selection rather than assuming the path exists in the working package.

Before selection, unavailable families are filtered out using required-member rules. Optional family members may be omitted, but a family with a missing required member is not eligible. If a selected family becomes unreadable, randomisation retries another eligible family and reports a concise failure only when no complete family remains. This exposes the full installed GlobalMorphs donor pool without sacrificing coherent map sets or introducing a package scan during randomisation.

Installed mod textures that satisfy discovery rules are first-class manual picker candidates. They join automatic randomisation only when they correspond to a known coherent corpus family; the first implementation does not guess relationships between arbitrarily named mod textures.

## Progress and failure handling

Builder progress reports a detached record containing:

- game;
- phase;
- packages processed and total packages;
- current package name; and
- verified texture count.

The settings view renders this through the existing progress bar and label. No invisible indexing task runs from package opening.

A failed first build leaves that game’s registry Missing/Failed and normal morph editing usable. A failed rebuild leaves the last verified registry active. The application log records build start, completion duration, cancellation, and failures, but the visible build window remains sufficient to understand current activity.

## Integration boundaries

- **Core:** detached registry candidates, occurrences, profile ranking, and search; no LEC package types.
- **LegendaryExplorer:** installed package enumeration, single-pass builder, metadata extraction, mount precedence, file serialization/verification, preview resolution, and later save-time porting.
- **WPF:** registry status/progress/actions, picker availability, search, and save orchestration.

The old OIDB provider/projector types are removed after the bespoke builder and tests replace every consumer. There is one discovery and persistence system, not parallel fallbacks.

## Verification

Deterministic tests must cover:

- case-insensitive discovery filtering and exact `Texture2D` admission;
- a package being opened no more than once per build;
- profile-declared shared-eye and Human HIR supplements;
- duplicate-path grouping and mount precedence;
- metadata, TFC storage flags, and external offsets surviving serialize/reopen;
- cancellation and failure preserving the previous file;
- atomic successful replacement;
- sequential Rebuild All and visible phase/package progress;
- malformed and unsupported registry rejection;
- missing registries never blocking face loading;
- ready registries loading without a package-indexing pass;
- profile ranking and picker search;
- every working-package `Texture2D` remaining available when the registry is missing or filtered;
- working-package candidates suppressing exact-path installed duplicates;
- randomisation resolving complete donor families from the installed registry rather than only the working package;
- unavailable randomisation families being excluded or retried; and
- local/current texture identity remaining distinct from genuinely external registry candidates.

One real installed-package build per game validates LEC package enumeration, metadata extraction, mount classification, and practical output size. The generated file size, texture count, package count, and elapsed time are recorded for comparison with the retired full OIDB workflow.

## Delivery order

1. Add the compact registry format, reader, and atomic serializer.
2. Replace the OIDB builder with the single-pass filtered registry builder and progress phases.
3. Rewire the existing management window and merge the working package with the compact registry in the runtime picker.
4. Route material randomisation family resolution through the merged catalogue.
5. Remove obsolete OIDB provider/projector code and background face-load indexing.
6. Run deterministic suites, then hand off an LE1 real-build and UI verification checkpoint.
7. Continue with path-preserving save-time texture materialisation.
