using System.IO;
using Microsoft.Win32;
using System.Windows;
using MorphFaceEditor.Views;
using MorphFaceEditor.ViewModels;

namespace MorphFaceEditor.Services;

public sealed class WpfEditorDialogService : IEditorDialogService
{
    private readonly TextureRegistrySettingsViewModel _textureRegistrySettings;

    public WpfEditorDialogService(TextureRegistrySettingsViewModel textureRegistrySettings) =>
        _textureRegistrySettings = textureRegistrySettings;
    public string? ChoosePackage(string? initialDirectory = null)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open a package containing BioMorphFace exports",
            Filter = "Mass Effect packages (*.pcc)|*.pcc|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : null
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public MorphPackageSaveRequest? ChooseMorphPackageDestination(string suggestedFileName, string sourcePackagePath)
    {
        var window = new SaveMorphToPccWindow(suggestedFileName, sourcePackagePath)
        {
            Owner = Application.Current.MainWindow
        };
        return window.ShowDialog() == true ? window.Request : null;
    }

    public MorphConversionSaveRequest? ChooseMorphConversionDestination(
        MorphFaceEditor.LegendaryExplorer.MorphFaceGame sourceGame,
        string suggestedFileName,
        string sourcePackagePath)
    {
        var window = new ConvertMorphWindow(sourceGame, suggestedFileName, sourcePackagePath)
        {
            Owner = Application.Current.MainWindow
        };
        return window.ShowDialog() == true ? window.Request : null;
    }

    public string? ChooseCloneName(string suggestedName, IReadOnlyCollection<string> existingObjectNames)
    {
        var window = new CloneMorphWindow(suggestedName, existingObjectNames)
        {
            Owner = Application.Current.MainWindow
        };
        return window.ShowDialog() == true ? window.ObjectName : null;
    }

    public string? ChooseMorphImportFile(string? initialDirectory = null)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import a BioMorphFace or baked mesh",
            Filter = "Supported morph files|*.ron;*.psk;*.pskx;*.gltf;*.glb;*.md5;*.md5mesh;*.me2headmorph;*.me3headmorph|RON head morph (*.ron)|*.ron|Mesh files (*.psk;*.pskx;*.gltf;*.glb;*.md5;*.md5mesh)|*.psk;*.pskx;*.gltf;*.glb;*.md5;*.md5mesh|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : null
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? ChooseRonExportFile(string suggestedFileName, string? initialDirectory = null)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export Morph as Trilogy Save Editor RON",
            Filter = "RON head morph (*.ron)|*.ron",
            DefaultExt = ".ron",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = suggestedFileName,
            InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : null
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? ChooseMeshExportDirectory(string? initialDirectory = null)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select a folder for the UModel mesh export",
            InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : null,
            Multiselect = false
        };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public bool ConfirmDeleteMorph(string facePath) =>
        new EditorMessageWindow(
            "Delete Morph",
            $"Trash '{facePath}' and all of its child exports?\n\nThe change remains temporary until you save the package.",
            confirmation: true,
            primaryLabel: "Delete Morph",
            secondaryLabel: "Cancel")
        {
            Owner = Application.Current.MainWindow
        }.ShowDialog() == true;

    public UnsavedChangesChoice ConfirmUnsavedChanges(
        string assetPath,
        UnsavedChangesScope scope = UnsavedChangesScope.Package)
    {
        var window = new UnsavedChangesWindow(assetPath, scope)
        {
            Owner = Application.Current.MainWindow
        };
        _ = window.ShowDialog();
        return window.Choice;
    }

    public void ShowInformation(string title, string message) =>
        _ = new EditorMessageWindow(title, message)
        {
            Owner = Application.Current.MainWindow
        }.ShowDialog();

    public void ShowObjectDatabaseSettings() =>
        _ = new TextureRegistrySettingsWindow(_textureRegistrySettings)
        {
            Owner = Application.Current.MainWindow
        }.ShowDialog();
}
