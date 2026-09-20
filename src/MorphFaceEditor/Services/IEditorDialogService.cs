namespace MorphFaceEditor.Services;

using MorphFaceEditor.LegendaryExplorer;

public interface IEditorDialogService
{
    string? ChoosePackage(string? initialDirectory = null);
    MorphPackageSaveRequest? ChooseMorphPackageDestination(string suggestedFileName, string sourcePackagePath);
    string? ChooseMeshPackageDestination(string suggestedFileName, string sourceMeshPath) => null;
    MorphConversionSaveRequest? ChooseMorphConversionDestination(
        MorphFaceGame sourceGame,
        string suggestedFileName,
        string sourcePackagePath);
    string? ChooseCloneName(string suggestedName, IReadOnlyCollection<string> existingObjectNames);
    string? ChooseMorphImportFile(string? initialDirectory = null);
    MorphFaceGame? ChooseStandaloneImportGame();
    RonImportDestinationChoice? ChooseRonImportDestination(
        MorphFaceGame targetGame,
        IReadOnlyList<RonNpcArchetypeOption> archetypes,
        bool allowPlayer);
    string? ChooseStandaloneMorphName(string suggestedName, IReadOnlyCollection<string> existingObjectNames);
    string? ChooseRonExportFile(string suggestedFileName, string? initialDirectory = null);
    string? ChooseMaterialImportFile(bool tse, string? initialDirectory = null) => ChooseMorphImportFile(initialDirectory);
    string? ChooseMaterialExportFile(bool tse, string suggestedFileName, string? initialDirectory = null) =>
        ChooseRonExportFile(suggestedFileName, initialDirectory);
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

public enum RonImportDestination
{
    PlayerWorkspace,
    NpcFace
}

public sealed record RonNpcArchetypeOption(string Key, string DisplayName);
public sealed record RonImportDestinationChoice(
    RonImportDestination Destination,
    string? NpcArchetypeKey);

public enum UnsavedChangesChoice
{
    Save,
    Discard,
    Cancel
}

public enum UnsavedChangesScope
{
    Face,
    StandaloneFace,
    DetachedMaterials,
    Package
}
