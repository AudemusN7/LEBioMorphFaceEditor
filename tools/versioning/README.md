# Versioning

Most of this document is LLM instruction for how to handle version bumping.

`Version.props` sets MFE's build version and the number in the title bar.

Use **major.feature.improvement.fix**, keeping major at zero until release. Numbers can go above 9; increasing one resets those after it to zero.

## Bump the version

From the repository root, bump an ordinary fix with:

```powershell
pwsh -File tools/versioning/Bump-Version.ps1
```

For larger changes, add `-Kind Improvement`, `-Kind Feature` or `-Kind Major`.

Bump every commit, including documentation, tooling, merges and amendments, and stage `Version.props` with the changes. A merge's version must exceed both parents. QA builds can use intermediate versions; gaps are fine.

## Enable the commit check

Run this once in each new clone:

```powershell
git config core.hooksPath .githooks
```

The hook rejects unchanged or lower staged versions and checks the resets.
