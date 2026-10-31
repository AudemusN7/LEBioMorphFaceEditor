# MFE regression tests

Use these tests to check individual MFE behaviours and saved data. For broader editing workflows, use the [QA harness](../MorphFaceEditor.QA/README.md).
```powershell
dotnet build MorphFaceEditor.slnx -c Release
dotnet run --project tools/MorphFaceEditor.Tests -c Release --no-build -- --list
dotnet run --project tools/MorphFaceEditor.Tests -c Release --no-build -- --suite ui UiWorkspaceTests
dotnet run --project tools/MorphFaceEditor.Tests -c Release --no-build -- --suite package "safe-save"
dotnet run --project tools/MorphFaceEditor.Tests -c Release --no-build -- --suite all
```

The default suite is `core`. Positional filters match test or class names without regard to case; multiple filters match any of the terms. `--list` uses the same suite and filters, so you can check the selection first. Unknown arguments or an empty selection exit with code 2; failed tests exit with code 1. Missing prerequisites are reported as skips or partial results, so exit code 0 doesn't mean those checks passed.

| Suite | Covers |
| --- | --- |
| `core` | Geometry and rig preservation, editing, Custom Mesh workspaces and randomisation. |
| `materials` | Material editing and preservation, plus focused pixel checks. |
| `registry` | Registry persistence, source identity, donor precedence and picker races. |
| `rendering` | Camera, lighting, effects and rendering resources. |
| `ui` | Workspaces, commands, cancellation, history, pickers and WPF presentation. |
| `package` | Transactions, corruption and identity checks, native decoding and import failures. |
| `conversion` | Prerequisites, attachments, directional donors and texture transfer. |
| `interchange` | RON, PCSAV preservation and material/file interchange. |
| `mesh` | Mesh geometry, rigs, topology, dependencies and Custom PCCs. |
| `tooling` | Compiler, Localiser and QA utility checks; these don't launch the QA harness. |

Some tests need installed LE games, current texture registries or local game and save fixtures. These inputs aren't included in the repository; missing prerequisites are reported in the results. Keep local fixtures and generated outputs out of Git. Persistence tests use temporary copies, while standalone workspaces also use MFE's AppData directory. Run from the checkout; direct executable invocation locates it automatically. Live UModel round trips belong in QA.

## Adding tests

Register each case in [TestCatalog.cs](TestCatalog.cs) under one suite. Extend an existing test for the specific behaviour where possible, and keep shared setup in `Support` or the relevant fixture. QA utility checks reference the actual QA assembly; randomisation runtime/compiler checks share `Core/RandomisationTests.cs`.

Test different failure cases and data that must survive an edit. Avoid duplicating full QA workflows or copying lookup tables, English labels and private shader layouts into tests. Check actual behaviour and saved content; a readable PCC alone doesn't prove its contents were preserved. Before removing a useful check, identify where it's still covered or explain why it's no longer needed.
