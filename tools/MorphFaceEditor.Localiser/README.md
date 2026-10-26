# MFE Localiser

A standalone Windows localisation editor for all ten supported languages, using MFE's authored dark palette and control styles. It depends on the shared localisation library, with no game, renderer or package dependencies.

Run `Run_MFE_Localiser.bat` from the repository root to build and open it, or open `bin/Release/net10.0-windows/MorphFaceEditor.Localiser.exe` after a Release build. An optional first argument selects a master XAML file.

When run from this checkout, it opens `src/MorphFaceEditor/Localisation/Strings.en-GB.xaml`, the single editable source catalogue. Outside the checkout, pass the English master path on the command line or use **Load localisation**. Selecting a language loads its adjacent `Strings.<code>.xaml` when available, then retains its draft in memory. **Load localisation** selects a different file for the current language. **Save localisation as** chooses its output path; `Ctrl+S` opens the same dialog. The save button's tooltip shows the current file path.

## Review workflow

1. Choose one of six filter tabs and a section from the left list; search scans keys, source/translated wording, notes and source references.
2. Edit wording and notes directly in the table. **English** is editable only in UK mode; **Localised** is blank/disabled in UK mode and editable for other languages. Hover a key for its source references.
3. Select rows with Shift/Ctrl (or Ctrl+A when the table has focus), then click an **Excluded** or **Reviewed** checkbox to apply that value to all selected rows. Clicking an unselected row's checkbox changes only that row. Space on a focused checkbox performs the same action. Tab moves between inline fields; Ctrl+A within a text field selects its text.
4. **Excluded** is shared master policy. **Reviewed** tracks the selected language; changing either its translation or its English source invalidates a translation's review. Excluded rows review the source wording. Blank/invalid translations cannot be marked reviewed.
5. Section colours prioritise invalid wording (red), any excluded string (orange), then fully reviewed sections (green); other sections use the default dark shade. Colours update for the active language.
6. Save to persist wording, notes and review marks. Saving a language also saves pending master edits/exclusions. Other language drafts remain in memory until saved, with a prompt on closing or replacing them. Missing and excluded translations are omitted from output, preserving UK fallback. Notes for missing translations are retained in XML comments.

Drag the divider beside the sections list to resize it. Right-click a key, English, Localised or Notes cell to **Copy cell**, **Copy key** or **Paste cell**. Paste cell replaces the entire editable value and has its own undo step; **Copy selection** and **Paste at caret** operate within the text editor. Keys and read-only wording cannot be pasted over. In other languages, **Needs review** hides excluded entries; the English master still includes excluded wording that needs source review.

The tool stores keyed JSON annotations in ordinary XML comments prefixed `localiser: `. WPF ignores them; MFE's shared reader enforces exclusions. Source reviews hash the wording, and translation reviews hash both source and translated wording, so external changes invalidate stale reviews. Resource keys stay fixed. Numbered placeholders and native file-filter patterns are checked before saving. Invalid imported translations remain editable for repair.

Master sections follow user workflows and control anatomy rather than source filenames. A section is an ordinary XML heading comment in the master; each control label sits beside its tooltip, with genuine profile-specific wording variants together. Keep these pairs together when adding entries. Reuse a key for the same control/action and wording. Notes explain non-obvious context, each numbered argument, and any native identifiers that must remain exact; omit notes that merely repeat the section or label. Preserve existing review/exclusion marks when moving rows; new wording remains unreviewed until Ryan reviews it.

Remove strings only after tracing their real UI consumers, including parameter-name lookup tables. Hidden future controls reuse existing wording where possible instead of reserving duplicate translations. An explicit master `localiser-retired: [...]` comment lists removed keys whose old translations may still exist. Only those listed keys are tolerated: they are never displayed or added to runtime resources, and are removed from a language file on its next deliberate save. Other unknown keys remain errors. This lets the English master be reviewed before the other language files are updated.

Product errors and warnings show localised explanations. Unstructured technical diagnostics from package libraries or the operating system go to AppData logs; they must not be inserted as untranslated fragments into UI wording. The app preserves specific catalogue-backed validation messages and structured package-layer reasons.

Saving validates adjacent candidates and checks fingerprints before replacing files. Previous files are retained as `.localiser.bak` backups. A combined language/master save rolls back the language if the master write fails. Concurrent changes block overwriting the loaded file; drafts can still be saved to a different path. The source corpus and locale scaffolds are not automatically edited or translated.

To use or ship edits, update the source catalogues and rebuild. All languages are embedded in `MorphFaceEditor.Localisation.dll`; MFE does not read loose dictionary overrides. Translations must preserve placeholders and excluded keys; the localisation tests validate the compiled catalogues and UK fallback.

## Validation

Build: `dotnet build MorphFaceEditor.slnx -c Release --no-restore` after restore.

Focused checks: `dotnet run --project tools/MorphFaceEditor.Tests -c Release --no-build -- --suite tooling localiser` and `--suite ui localisation "WPF resources"`.

The routine tests use temporary copies of the full master and a hidden WPF window; they do not launch the product apps or modify the source corpus.
