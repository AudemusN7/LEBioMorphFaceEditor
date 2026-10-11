# MFE version contract

`Version.props` is the build version oracle. The four numbers are major release,
significant feature threshold, improvements within that threshold, and small fixes.
Every commit increases the version, including documentation, tooling and merges.
A higher component resets the lower components to zero. Components are numeric,
not decimal digits: a fix counter may exceed 9. Major stays zero until release.

Run `pwsh -File tools/versioning/Bump-Version.ps1` for a fix, or pass
`-Kind Improvement`, `Feature` or `Major` for the corresponding boundary.
Stage `Version.props` with the change. Install the local commit guard with
`git config core.hooksPath .githooks`; it validates the staged version against all
parents. For merges, increment from the highest parent version. Amending a commit
also requires a new version so changed builds never share a version. New clones need
the hook installation too; Git does not activate repository hooks automatically.

Assembly, file and informational versions derive from this file. The title bar
combines the existing translated application name with the built assembly version.
Historical assignments are recorded in `VersionHistory.csv` (original commit IDs).
