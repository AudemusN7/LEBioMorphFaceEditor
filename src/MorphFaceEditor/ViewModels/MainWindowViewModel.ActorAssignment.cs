using System.Text;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.ViewModels;

public sealed partial class MainWindowViewModel
{
    private Task AssignMorphToActorAsync() => AssignToActorAsync(ActorAssignmentMode.Morph);

    private Task AssignMaterialsToActorAsync() => AssignToActorAsync(ActorAssignmentMode.Materials);

    private async Task AssignToActorAsync(ActorAssignmentMode mode)
    {
        if (SelectedFace is not { } source || WorkspacePackagePath is not { } workspacePath)
        {
            return;
        }
        if (!await FlushSelectedExportSourceAsync(source))
        {
            return;
        }

        ActorAssignmentInventory inventory;
        IsBusy = true;
        ErrorMessage = null;
        Status = Loc.Text(mode == ActorAssignmentMode.Morph
            ? "loc_Shell_Status_FindingCompatibleAuthoredActors"
            : "loc_Shell_Status_FindingSafeLocalMaterials");
        try
        {
            inventory = await Task.Run(() => _actorAssignmentService.ReadInventory(
                workspacePath, source.InstancedPath, source.ProfileKey));
        }
        catch (Exception exception)
        {
            AppLog.Error($"Actor-assignment preflight failed for '{source.InstancedPath}'.", exception);
            ErrorMessage = Loc.Format("loc_Shell_Error_ActorAssignmentCouldNotBe", exception.Message);
            Status = Loc.Text("loc_Shell_Status_ActorAssignmentPreflightFailedThe");
            return;
        }
        finally
        {
            IsBusy = false;
        }

        var candidate = _dialogs.ChooseActorAssignment(inventory, mode);
        if (candidate is null)
        {
            Status = Loc.Text("loc_Shell_Status_ActorAssignmentCancelledTheWorkspace");
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        Status = Loc.Format(mode == ActorAssignmentMode.Morph
            ? "loc_Shell_Status_AssigningMorphToActor"
            : "loc_Shell_Status_AssigningMaterialOverridesToActor",
            source.DisplayName, candidate.DisplayName);
        var packageModified = false;
        try
        {
            if (mode == ActorAssignmentMode.Morph)
            {
                var result = await Task.Run(() => _actorAssignmentService.AssignMorph(
                    workspacePath, source.InstancedPath, source.ProfileKey, candidate.UIndex));
                packageModified = true;
                MarkWorkspaceChanged();
                if (!await RefreshWorkspaceAsync(source.InstancedPath))
                {
                    ErrorMessage = Loc.Text("loc_Shell_ErrorMessage_TheMorphWasAssignedBut");
                    Status = Loc.Text("loc_Shell_Status_MorphAssignmentCompletedWorkspaceReload");
                    return;
                }
                Status = Loc.Format("loc_Shell_Status_AssignedAndVerifiedOnSave", result.NewMorphPath, candidate.DisplayName);
                AppLog.Information(
                    $"Actor morph assignment verified: '{result.OwnerPath}.{result.PropertyName}' -> '{result.NewMorphPath}'.");
            }
            else
            {
                var result = await Task.Run(() => _actorAssignmentService.AssignMaterials(
                    workspacePath, source.InstancedPath, source.ProfileKey, candidate.UIndex));
                packageModified = true;
                MarkWorkspaceChanged();
                if (!await RefreshWorkspaceAsync(source.InstancedPath))
                {
                    ErrorMessage = Loc.Text("loc_Shell_ErrorMessage_TheMaterialsWereAssignedBut");
                    Status = Loc.Text("loc_Shell_Status_MaterialAssignmentCompletedWorkspaceReload");
                    return;
                }
                var changedCount = result.ChangedMaterials.Select(value => value.UIndex).Distinct().Count();
                Status = Loc.Format("loc_Shell_Status_AssignedAndVerifiedMaterialOverrides", changedCount);
                _dialogs.ShowInformation(Loc.Text("loc_Shell_DialogTitle_MaterialsAssigned"), BuildMaterialCompletion(result));
                AppLog.Information(
                    $"Actor material assignment verified for '{result.ActorPath}' on {changedCount} local MIC(s).");
            }
        }
        catch (Exception exception)
        {
            ReportContextOperationFailure(
                mode == ActorAssignmentMode.Morph ? "loc_Shell_Operation_AssignMorphToActor" : "loc_Shell_Operation_AssignMaterialsToActor",
                mode == ActorAssignmentMode.Morph
                    ? "loc_Shell_Error_MorphCouldNotAssign"
                    : "loc_Shell_Error_MaterialsCouldNotAssign",
                exception,
                packageModified);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string BuildMaterialCompletion(ActorMaterialAssignmentResult result)
    {
        var changed = result.ChangedMaterials
            .DistinctBy(value => value.UIndex)
            .ToArray();
        var text = new StringBuilder()
            .AppendLine(Loc.Format("loc_Shell_ActorAssignment_Actor", result.ActorPath))
            .AppendLine(Loc.Format("loc_Shell_ActorAssignment_SourceOverride", result.SourceMaterialOverridePath))
            .AppendLine(Loc.Format("loc_Shell_ActorAssignment_ChangedLocalMics", changed.Length))
            .AppendLine(Loc.Format("loc_Shell_ActorAssignment_CategoriesReplaced", string.Join(", ", new[]
            {
                result.ReplacedTextures ? Loc.Text("loc_Shell_ActorAssignment_Textures") : null,
                result.ReplacedVectors ? Loc.Text("loc_Shell_ActorAssignment_Vectors") : null,
                result.ReplacedScalars ? Loc.Text("loc_Shell_ActorAssignment_Scalars") : null
            }.Where(value => value is not null))))
            .AppendLine()
            .AppendLine(Loc.Text("loc_Shell_ActorAssignment_Changed"));
        foreach (var target in changed)
        {
            text.AppendLine(Loc.Format("loc_Shell_ActorAssignment_ChangedRow",
                target.UIndex, target.InstancedPath, LocalizedComponentRole(target.ComponentRole), target.SlotIndex, target.Family));
        }
        if (changed.Any(target => target.ComponentRole == ActorComponentRole.Body))
        {
            text.AppendLine(Loc.Text("loc_Shell_ActorAssignment_BodySkinOnly"));
        }
        text.AppendLine().AppendLine(Loc.Format("loc_Shell_ActorAssignment_SkippedSlots", result.SkippedMaterials.Count));
        foreach (var skipped in result.SkippedMaterials)
        {
            text.AppendLine(Loc.Format("loc_Shell_ActorAssignment_SkippedRow",
                skipped.UIndex, skipped.InstancedPath, skipped.Reason));
        }
        text.AppendLine().Append(Loc.Text("loc_Shell_ActorAssignment_TemporaryWorkspaceSaveNote"));
        return text.ToString();
    }

    private static string LocalizedComponentRole(ActorComponentRole role) => role switch
    {
        ActorComponentRole.Head => Loc.Text("loc_VM_actorAssignment_componentRoleHead"),
        ActorComponentRole.Hair => Loc.Text("loc_VM_actorAssignment_componentRoleHair"),
        ActorComponentRole.Body => Loc.Text("loc_VM_actorAssignment_componentRoleBody"),
        _ => Loc.Text("loc_VM_actorAssignment_componentRoleOther")
    };
}
