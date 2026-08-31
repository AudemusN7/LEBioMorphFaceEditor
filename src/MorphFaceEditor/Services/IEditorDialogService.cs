namespace MorphFaceEditor.Services;

using MorphFaceEditor.LegendaryExplorer;

public interface IEditorDialogService
{
    string? ChoosePackage(string? initialDirectory = null);
    MorphPackageSaveRequest? ChooseMorphPackageDestination(string suggestedFileName, string sourcePackagePath);
    MorphConversionSaveRequest? ChooseMorphConversionDestination(
        MorphFaceGame sourceGame,
        string suggestedFileName,
        string sourcePackagePath);
    string? ChooseCloneName(string suggestedName, IReadOnlyCollection<string> existingObjectNames);
    string? ChooseMorphImportFile(string? initialDirectory = null);
    string? ChooseRonExportFile(string suggestedFileName, string? initialDirectory = null);
    string? ChooseMeshExportDirectory(string? initialDirectory = null);
    ActorAssignmentCandidate? ChooseActorAssignment(
        ActorAssignmentInventory inventory,
        ActorAssignmentMode mode);
    bool ConfirmDeleteMorph(string facePath);
    UnsavedChangesChoice ConfirmUnsavedChanges(
        string assetPath,
        UnsavedChangesScope scope = UnsavedChangesScope.Package);
    void ShowInformation(string title, string message);
    void ShowTextureRegistrySettings();
}

public sealed record MorphPackageSaveRequest(string DestinationPackagePath, bool CreateNewPackage);

public sealed record MorphConversionSaveRequest(
    MorphFaceGame TargetGame,
    string DestinationPackagePath,
    bool CreateNewPackage,
    string? TemplatePackagePath);

public enum UnsavedChangesChoice
{
    Save,
    Discard,
    Cancel
}

public enum UnsavedChangesScope
{
    Face,
    Package
}
