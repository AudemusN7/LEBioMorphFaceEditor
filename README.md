# Morph Face Editor

Morph Face Editor (MFE) is a Windows tool for editing Shepard and NPC faces in Mass Effect Legendary Edition, with a live 3D preview. For most players, opening a Shepard save (`.pcsav`) is the easiest place to start. Mod developers can also work with NPC faces and custom head meshes.

**Download:** [Nexus Mods](https://www.nexusmods.com/masseffectlegendaryedition/mods/3477) · [GitHub Releases](https://github.com/AudemusN7/LEBioMorphFaceEditor/releases)

![Morph Face Editor editing a Shepard career save](documentation/images/player-workspace.png)

## Core features

- Edit Shepard directly in a PC save, or import headmorphs previously exported from save editors or MFE itself.
- Create and edit Human Male/Female, Asari, Salarian, Turian, Krogan, Batarian and Vorcha NPC faces.
- Change face shape, materials, textures, hair and accessories, with Undo/Redo while you experiment.
- Randomise a face or selected features. Try **Cursed Mode** when conventional beauty standards are simply too tame for you.
- Assign NPC faces and materials to actors, and convert compatible faces between LE1, LE2 and LE3.
- Share headmorphs and material presets, or import PSK/glTF meshes for advanced material work.

## Requirements

- 64-bit Windows.
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) — choose the Windows **x64** installer.
- Mass Effect Legendary Edition (Steam/EA/Game Pass etc)

MFE uses meshes and textures from your installed games and mods. Game assets aren't included with the editor.

## Documentation

Start with **Getting Started**, then follow the guide for your workflow.

| Guide | Level |
| --- | --- |
| [Getting Started](documentation/GETTING-STARTED.md) | Beginner |
| [Player Faces](documentation/PLAYER-FACES.md) | Beginner |
| [Editing Faces](documentation/EDITING-FACES.md) | Beginner, with advanced sections |
| [NPC Faces and Actor Assignment](documentation/NPC-FACES.md) | Intermediate |
| [Custom Meshes](documentation/CUSTOM-MESHES.md) | Advanced |
| [Materials, Textures and Attachments](documentation/MATERIALS-TEXTURES-AND-ATTACHMENTS.md) | Detailed reference |
| [Saving, Exporting and Conversion](documentation/SAVING-EXPORTING-AND-CONVERSION.md) | Reference |
| [Shortcuts and Gestures](documentation/SHORTCUTS-AND-GESTURES.md) | Reference |
| [Troubleshooting and Limitations](documentation/TROUBLESHOOTING-AND-LIMITATIONS.md) | Reference |

## Launching the editor

1. Extract the complete release archive into a folder. Keep its files together.
2. Run **MorphFaceEditor.exe**.
3. On the welcome screen, click **Build Mesh/Texture Databases**.
4. Click **Rebuild** beside the game you need, or **Rebuild All** for all detected games. Close the database window once the scan finishes.
5. Open your file through **File → Open File**, or drag it onto the editor.

The scan finds assets without changing game files. After installing or removing mods, use **Mesh/Texture Databases → Rescan Mods**.

## Saving your work

Use **File → Save File** (Ctrl+S) to update an open save or PCC, or **Save As** (Ctrl+Shift+S) for a separate copy.

Exports are separate files. Follow the Player or NPC guide above to apply them in game.

## Limitations

- Career-save editing supports Legendary Edition PC saves.
- Available controls depend on the face. Use material editing for Vorcha; morph sliders aren't available.
- The preview closely approximates the game, but lighting and animation can differ. Check the result in game.
- MFE saves and exports files; it doesn't install the finished mod.

## Problems or questions

Check [Troubleshooting and Limitations](documentation/TROUBLESHOOTING-AND-LIMITATIONS.md) first. Report problems through [GitHub Issues](https://github.com/AudemusN7/LEBioMorphFaceEditor/issues) or the Nexus mod page. Include the steps that caused it and a diagnostic ZIP from **File → Settings → Export Logs**.

## Special thanks

- **Mgamerz** — LEC support, advice and the inspiration for Cursed Mode.
- **DropTheSquid** — glTF export reference and BioMorphFace experiments.
- **HenBagle** — BioMorphFace experiments.
- **Karlitos** — Trilogy Save Editor formats and CLI.
- **BioWare** — For everything. Ad Astra Per Aspera.

For third-party credits and licences, open **About → About MFE** in the editor.

# AI Diclosure
This tool was developed over the course of 3 months with extensive assistance from OpenAI's Codex, which wrote the majority of the application's code. However, all features, functionality and design decisions were directed by me, with extensive hands-on testing, debugging and validation throughout development. While AI was responsible for much of the implementation, the vision, quality standards and responsibility for the finished application remain entirely my own based on my own extensive knowledge of Mass Effect modding, and backed by Legendary Explorer's human-written code as foundational.
