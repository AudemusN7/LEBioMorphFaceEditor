# MFE regression tests

This executable protects focused behaviour, failure boundaries and deterministic native-asset regressions. Run broad game/archetype journeys through the [QA harness](../MorphFaceEditor.QA/README.md); inspect generic package data with LECCLI. These have different jobs and should not grow parallel versions of the same workflow.

```powershell
dotnet build MorphFaceEditor.slnx -c Release
dotnet run --project tools/MorphFaceEditor.Tests -c Release --no-build -- --list
dotnet run --project tools/MorphFaceEditor.Tests -c Release --no-build -- --suite ui UiWorkspaceTests
dotnet run --project tools/MorphFaceEditor.Tests -c Release --no-build -- --suite package "safe-save"
dotnet run --project tools/MorphFaceEditor.Tests -c Release --no-build -- --suite all
```

The default suite is `core`. Positional filters match test names **or class names**, case-insensitively; multiple filters are ORed. `--list` respects the same suite and filters, so inspect the selection before running it. Unknown arguments and empty selections exit 2. Failed tests exit 1. Missing prerequisites remain explicit skips/partial results; exit 0 does not turn those into passes.

| Suite / folder | Primary responsibility |
|---|---|
| `core` / `Core` | Authored geometry/rig preservation, semantic editing, Custom scopes and randomisation safety. |
| `materials` / `Materials` | Material editing/preservation and focused species/channel pixel regressions. |
| `registry` / `Registry` | Atomic registry persistence, source identity, donor precedence and picker races. |
| `rendering` / `Rendering` | Camera, lighting, HDR/AO/shadows, native rendering and resource lifecycle. |
| `ui` / `Ui` | Workspace transitions, command boundaries, cancellation, history, picker races and WPF presentation. |
| `package` / `Packages` | Transactions, corruption/identity guards, native decoding, standalone/legacy import failure boundaries. |
| `conversion` / `Conversion` | Missing prerequisites, rejected attachments, exact directional donors and texture-transfer contracts. |
| `interchange` / `Interchange` | RON schemas, native PCSAV preservation and material/file interchange contracts. |
| `mesh` / `Mesh` | Decoder/writer geometry, rig and topology preservation; exact dependency and Custom PCC contracts. Replaces the old `c4` suite. |
| `tooling` / `Tooling` | Compiler, Localiser and QA utility behaviour. These do not launch the QA harness. |

[TestCatalog.cs](TestCatalog.cs) is the single registration map. Every case belongs to one suite. Shared setup belongs in `Support` or the relevant material/rendering fixture; it must not become a second implementation of product behaviour. QA utility checks reference the actual QA assembly instead of compiling copied helper sources. Randomisation's runtime/compiler checks share their fixture owner in `Core/RandomisationTests.cs`.

Game-backed tests require the local `fixtures/Global Morphs` corpus, installed LE games and current texture registries. Live UModel round trips belong to QA. Native-save tests also use `fixtures/PCSAV_Test`. Fixtures and generated outputs stay out of Git. Persistence tests edit temporary copies; standalone workspaces also use MFE's AppData directory. Run from the checkout; direct executable invocation locates its checkout automatically.

## Coverage ownership

QA owns successful game/archetype conversion, randomisation, Custom material, external TSE RON, Player/NPC RON, clipboard/save and mesh interchange journeys, plus exhaustive donor sweeps. Its initial-import comparisons protect original geometry, materials and attachment identities before any later export can disguise a loss. The external RON journey also owns Ashley's canonical MIC, malformed Sheploo warnings, creator aliases and installed-source fingerprints.

The regression suite retains codec rejection, captured-file handling, source conflicts, Save As adoption, cancelled/failed deletion, unknown data, LOD/order preservation, topology gates, native graph details and exact donor identities. Its pixel tests protect selected shader failures independently of QA preview images. Repeated successful journeys, mirrored lookup tables and low-value metadata/default or parameter pixel snapshots were deliberately retired; they do not each require an equivalent replacement.

When adding coverage:

- Reproduce the specific behaviour and extend its existing owner. Prefer an extra input row or assertion over another fixture engine or full journey.
- Keep tests for distinct failure/preservation boundaries. A method name or implementation branch alone is not a reason for a test.
- Avoid snapshots of English labels, private shader field layouts and copies of production lookup tables. Assert visible capability, ordering, identity or rendered behaviour instead.
- Before removing a meaningful failure or preservation check, name its retained owner or record why it is being retired. Transfer important assertions into an existing scenario where possible; a unique minor assertion alone does not justify another test. A passing save or readable PCC alone does not prove preserved content.
