# MFE Localiser

Use the Localiser to edit MFE's ten languages on Windows.

Run `dotnet run --project tools/MorphFaceEditor.Localiser -c Release` from the repository root to build and open it. To specify a master XAML file, add `--` followed by its path.

In a checkout, it opens the English master at `src/MorphFaceEditor/Localisation/Strings.en-GB.xaml`. Selecting a language loads the adjacent `Strings.<code>.xaml` if available. Use **Load localisation** to choose another file for the current language, and **Save localisation as** or **Ctrl+S** to choose an output path. Hover over the save button to see the current path.

## Review and translate

1. Choose a filter and section. Search covers keys, wording, notes and references; hover over a key to see its references.
2. Edit wording and notes in the table. **English** is editable in UK mode; **Localised** is editable for the other languages. The **Excluded** filter and column appear only in UK mode.
3. Select rows with **Shift/Ctrl**, or **Ctrl+A** with the table focused. Click an **Excluded** or **Reviewed** checkbox to apply it to the selection. An unselected checkbox changes only that row. **Space** toggles a focused checkbox; **Ctrl+A** inside a text field selects its text.
4. Save your changes. Saving a language also saves pending master edits and exclusions. Other drafts stay in memory; you'll be prompted before closing or replacing them.

**Excluded** applies to every language. Excluded entries stay hidden outside UK mode. Saving omits missing and excluded translations so MFE falls back to English, while keeping notes for missing translations in XML comments.

**Reviewed** applies to the selected language. Changing the translation or English source clears its review. Blank or invalid translations can't be reviewed; excluded rows still need their English reviewed. Section colours show invalid wording first (red), exclusions next (orange), then fully reviewed sections (green).

Drag the divider to resize the section list. Right-click a cell for **Copy cell**, **Copy key** or **Paste cell**. Paste replaces an editable value with its own undo step. Use **Copy selection** and **Paste at caret** within a text field.

## Maintaining the catalogue

Keep labels beside their tooltips and related wording together. Reuse keys for identical controls and wording. Use notes for context, numbered arguments and identifiers that must stay exact. Preserve review/exclusion marks when moving rows, and review new wording before translating it.

Check where a string is used before removing it. Add removed keys to the master's `localiser-retired: [...]` comment if old translations still contain them. Retired keys are ignored and removed on the language file's next save; other unknown keys are errors.

Review and exclusion data lives in `localiser: ` XML comments. Preserve numbered placeholders and file-filter patterns in translations. Invalid imports stay editable for repair.

Saving keeps `.localiser.bak` backups and checks for external changes. If a combined save fails on the master, it rolls back the language write. Save your draft elsewhere if another program has changed the loaded file.

To use or ship edits, update the source catalogues and rebuild. Languages are embedded in `MorphFaceEditor.Localisation.dll`; MFE doesn't read loose dictionary overrides.

## Validation

After restoring, build with `dotnet build MorphFaceEditor.slnx -c Release --no-restore`. Run the focused checks with:
```powershell
dotnet run --project tools/MorphFaceEditor.Tests -c Release --no-build -- --suite tooling localiser
dotnet run --project tools/MorphFaceEditor.Tests -c Release --no-build -- --suite ui localisation "WPF resources"
```
These checks use temporary catalogue copies.