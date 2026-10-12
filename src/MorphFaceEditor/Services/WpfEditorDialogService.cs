using System.IO;
using Microsoft.Win32;
using System.Windows;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Views;
using MorphFaceEditor.ViewModels;
using MorphFaceEditor.Infrastructure;

namespace MorphFaceEditor.Services;

public sealed class WpfEditorDialogService : IEditorDialogService
{
    public bool ConfirmCreateCareerMorph(string name) => new EditorMessageWindow(
        Loc.Text("loc_View_MainWindow_OpenPackage"), Loc.Format("loc_Pcsav_ConfirmCreate", name),
        confirmation: true, primaryLabel: Loc.Text("loc_StandaloneImportGameWindow_Continue"),
        secondaryLabel: Loc.Text("loc_Common_Cancel")) { Owner = Application.Current.MainWindow }.ShowDialog() == true;

    public string? ChooseSaveAsFile(string sourcePath)
    {
        var extension = Path.GetExtension(sourcePath);
        var dialog = new SaveFileDialog
        {
            Title = Loc.Text("loc_FileSave_SaveAs"), FileName = Path.GetFileName(sourcePath),
            DefaultExt = extension, AddExtension = true, OverwritePrompt = true,
            InitialDirectory = Path.GetDirectoryName(sourcePath),
            Filter = $"{extension.TrimStart('.').ToUpperInvariant()} (*{extension})|*{extension}"
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
    public bool ConfirmDeleteCareerMorph(string name) => new EditorMessageWindow(
        Loc.Text("loc_WpfEditorDialogService_DeleteMorphTitle"), Loc.Format("loc_Pcsav_ConfirmDelete", name),
        confirmation: true, primaryLabel: Loc.Text("loc_WpfEditorDialogService_DeleteMorphTitle"),
        secondaryLabel: Loc.Text("loc_Common_Cancel")) { Owner = Application.Current.MainWindow }.ShowDialog() == true;
    public MeshSaveChoice? ChooseMeshSaveFormat()
    {
        var window = new MeshSaveFormatWindow { Owner = Application.Current.MainWindow };
        return window.ShowDialog() == true ? window.Choice : null;
    }
    private readonly TextureRegistrySettingsViewModel _textureRegistrySettings;

    public WpfEditorDialogService(TextureRegistrySettingsViewModel textureRegistrySettings) =>
        _textureRegistrySettings = textureRegistrySettings;
    public string? ChoosePackage(string? initialDirectory = null)
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.Text("loc_WpfEditorDialogService_OpenPackageTitle"),
            Filter = EditorFileDrop.WorkspaceOpenFilter,
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

    public string? ChooseMeshPackageDestination(string suggestedFileName, string sourceMeshPath)
    {
        var dialog = new SaveFileDialog
        {
            Title = Loc.Text("loc_WpfEditorDialogService_SaveMeshMaterialsTitle"),
            Filter = EditorFileDrop.PackageFilter,
            DefaultExt = ".pcc",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = suggestedFileName,
            InitialDirectory = Directory.Exists(Path.GetDirectoryName(sourceMeshPath))
                ? Path.GetDirectoryName(sourceMeshPath)
                : null
        };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
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
            Title = Loc.Text("loc_WpfEditorDialogService_ImportMorphTitle"),
            Filter = EditorFileDrop.MorphImportFilter,
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : null
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? ChooseWorkspaceFile(string? initialDirectory = null)
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.Text("loc_WpfEditorDialogService_OpenWorkspaceTitle"),
            Filter = EditorFileDrop.WorkspaceOpenFilter,
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : null
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public NewPlayerMorphChoice? ChooseNewPlayerMorph()
    {
        var window = new StandaloneImportGameWindow(newPlayerMode: true) { Owner = Application.Current.MainWindow };
        return window.ShowDialog() == true && window.SelectedGame is { } game
            ? new NewPlayerMorphChoice(game, window.SelectedSex) : null;
    }

    public MorphFaceGame? ChooseStandaloneImportGame()
    {
        var window = new StandaloneImportGameWindow
        {
            Owner = Application.Current.MainWindow
        };
        return window.ShowDialog() == true ? window.SelectedGame : null;
    }

    public RonImportDestinationChoice? ChooseRonImportDestination(
        MorphFaceGame targetGame,
        IReadOnlyList<RonNpcArchetypeOption> archetypes,
        bool allowPlayer)
    {
        var window = new RonImportDestinationWindow(targetGame, archetypes, allowPlayer)
        {
            Owner = Application.Current.MainWindow
        };
        return window.ShowDialog() == true && window.Destination is { } destination
            ? new RonImportDestinationChoice(destination, window.SelectedNpcArchetypeKey)
            : null;
    }

    public string? ChooseNewPlayerMorphName(string suggestedName, IReadOnlyCollection<string> existingObjectNames)
    {
        var window = new CloneMorphWindow(suggestedName, existingObjectNames, importMode: true, newPlayerMode: true)
        { Owner = Application.Current.MainWindow };
        return window.ShowDialog() == true ? window.ObjectName : null;
    }

    public string? ChooseStandaloneMorphName(
        string suggestedName,
        IReadOnlyCollection<string> existingObjectNames)
    {
        var window = new CloneMorphWindow(suggestedName, existingObjectNames, importMode: true)
        {
            Owner = Application.Current.MainWindow
        };
        return window.ShowDialog() == true ? window.ObjectName : null;
    }

    public string? ChooseRonExportFile(string suggestedFileName, string? initialDirectory = null)
    {
        var dialog = new SaveFileDialog
        {
            Title = Loc.Text("loc_WpfEditorDialogService_ExportMorphRonTitle"),
            Filter = EditorFileDrop.RonMorphFilter,
            DefaultExt = ".ron",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = suggestedFileName,
            InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : null
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? ChooseMaterialImportFile(bool tse, string? initialDirectory = null)
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.Text(tse ? "loc_WpfEditorDialogService_ImportTseRonTitle" : "loc_WpfEditorDialogService_ImportMaterialsTitle"),
            Filter = tse ? EditorFileDrop.TseRonFilter : EditorFileDrop.RonMaterialFilter,
            InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : null,
            Multiselect = false
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? ChooseMaterialExportFile(bool tse, string suggestedFileName, string? initialDirectory = null)
    {
        var dialog = new SaveFileDialog
        {
            Title = Loc.Text(tse ? "loc_WpfEditorDialogService_ExportTseRonTitle" : "loc_WpfEditorDialogService_ExportMaterialsTitle"),
            Filter = tse ? EditorFileDrop.TseRonFilter : EditorFileDrop.RonMaterialFilter,
            DefaultExt = ".ron", AddExtension = true, OverwritePrompt = true,
            FileName = suggestedFileName,
            InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : null
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? ChooseMeshExportDirectory(string? initialDirectory = null)
    {
        var dialog = new OpenFolderDialog
        {
            Title = Loc.Text("loc_WpfEditorDialogService_UModelExportFolderTitle"),
            InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : null,
            Multiselect = false
        };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public ActorAssignmentCandidate? ChooseActorAssignment(
        ActorAssignmentInventory inventory,
        ActorAssignmentMode mode)
    {
        var window = new ActorAssignmentWindow(inventory, mode)
        {
            Owner = Application.Current.MainWindow
        };
        return window.ShowDialog() == true ? window.SelectedCandidate : null;
    }

    public bool ConfirmDeleteMorph(string facePath, bool hasReferences = false) =>
        new EditorMessageWindow(
            Loc.Text("loc_WpfEditorDialogService_DeleteMorphTitle"),
            Loc.Format(hasReferences
                ? "loc_WpfEditorDialogService_DeleteReferencedMorphMessage"
                : "loc_WpfEditorDialogService_DeleteMorphMessage", facePath),
            confirmation: true,
            primaryLabel: Loc.Text("loc_WpfEditorDialogService_DeleteMorphTitle"),
            secondaryLabel: Loc.Text("loc_Common_Cancel"))
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

    public bool ConfirmHairScalpTextureSwitch(string hairMeshPath) =>
        ConfirmHairScalpTextureSwitch(hairMeshPath, switchMaterialGraph: false);

    public bool ConfirmHairScalpTextureSwitch(string hairMeshPath, bool switchMaterialGraph) =>
        new EditorMessageWindow(
            Loc.Text("loc_WpfEditorDialogService_MatchScalpTexturesTitle"),
            Loc.Format(switchMaterialGraph
                ? "loc_WpfEditorDialogService_MatchScalpTexturesAndMaterialMessage"
                : "loc_WpfEditorDialogService_MatchScalpTexturesMessage", hairMeshPath),
            confirmation: true,
            primaryLabel: Loc.Text("loc_WpfEditorDialogService_SwitchScalp"),
            secondaryLabel: Loc.Text("loc_WpfEditorDialogService_KeepCurrent"))
        {
            Owner = Application.Current.MainWindow
        }.ShowDialog() == true;

    public void ShowInformation(string title, string message) =>
        _ = new EditorMessageWindow(title, message)
        {
            Owner = Application.Current.MainWindow
        }.ShowDialog();

    public void ShowTextureRegistrySettings() =>
        _ = new TextureRegistrySettingsWindow(_textureRegistrySettings)
        {
            Owner = Application.Current.MainWindow
        }.ShowDialog();
}
