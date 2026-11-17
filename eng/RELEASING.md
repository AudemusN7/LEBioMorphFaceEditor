# Automatic releases

Each push builds its resulting tip once. `main` publishes stable updates on GitHub and Nexus; `dev` publishes GitHub beta prereleases for testers. Merging multiple `dev` commits into `main` builds only the resulting main tip. Other branches do not publish. The version comes from `Version.props`; the full tip commit message becomes the release notes and, for stable updates, the Nexus file description and changelog. Write a combined changelog in the merge commit message when releasing several changes together.

The installed `.githooks/post-commit` and `post-merge` automatically push `main` and `dev` to `origin`. New clones need `git config core.hooksPath .githooks` once. They never force-push or merge branches. If a network error prevents a push, the local commit remains saved; retry with `git push origin main` or `git push origin dev`.

## Tester betas

Dev builds use unique tags and filenames such as `v1.0.0.3-beta.0123456789ab`, combining the numeric app version with the first twelve source-commit characters. Each beta has its own permanent GitHub release page and ZIP link to share with a tester. It is marked as a prerelease and never marked latest. Betas do not upload to Nexus or change the stable update channel; MFE's existing update checker reads stable releases only. The app's assembly version stays numeric so a later stable version can still be compared normally.

## Pipeline

`.github/workflows/release.yml` runs on a GitHub-hosted Windows runner with the SDK in `global.json`. It verifies pinned dependencies, rejects native Debug runtime imports, publishes the editor, copies the README and user documentation, and checks every ZIP entry against its source. Development tools, game packages, PDBs and the build licence are excluded. The archive, SHA-256 checksum and source manifest are retained as workflow artifacts.

The GitHub release starts as a draft. Nexus receives the same ZIP with the main file category, archives the previous version and updates the mod version. After Nexus succeeds, GitHub publishes the release and marks it latest. Release runs queue rather than cancel an active upload. Old version numbers are rejected to prevent accidental downgrades. Give every resulting main commit a new four-part version.

Nexus uses the official [`Nexus-Mods/upload-action`](https://github.com/Nexus-Mods/upload-action), pinned to a reviewed commit, with repository secret `NEXUSMODS_API_KEY`. The desktop uploader is not needed on the runner. Target IDs are file `8326725`, mod `15942918606229` (MFE page `3477`); short file ID `b0WPonGD8` is a web identifier, not an action input. Upload keys must never be placed in source files or commit messages.

Use **Actions → Release MFE → Run workflow → Build and verify the package without publishing** on `main` or `dev` to test a clean hosted build. This is enabled by default for manual runs. To retry publication after a fix, rerun the failed job or manually run on the intended branch with that option unchecked. Completed uploads are detected and skipped.

For local packaging validation only:

```powershell
pwsh -File eng/Build-Release.ps1 -AllowDirty -OutputDirectory artifacts/local-release-check
```

Add `-Channel Beta` to validate beta packaging. Dirty validation packages are marked in their manifest and cannot be published. Choose a fresh output directory for another run. Packaging validation does not launch the editor or run the QA harness. A dev beta can be committed and shared to obtain tester confirmation; Audemus's hands-on QA remains before merging a stable public update.

## Interrupted Nexus uploads

An upload attempt is recorded in an HTML comment on the GitHub draft before contacting Nexus. If Nexus fails, reruns stop rather than guess whether a version was created and upload it twice. Inspect the run log and Nexus file versions first:

- If no version was created, remove only `<!-- mfe-nexus:started -->` from the draft notes and rerun.
- If both the file version and changelog were published, append `<!-- mfe-nexus:complete:VERSION_ID -->` using the returned numeric Nexus version ID, then rerun. The original GitHub assets are retained and only the draft publication resumes.
- If the file uploaded but its changelog failed, finish that changelog on Nexus before marking the upload complete.

Keep `<!-- mfe-source:COMMIT_SHA -->` intact. A release or tag attached to other code is never overwritten. A GitHub failure after successful Nexus publication can be retried without repeating the Nexus upload.
