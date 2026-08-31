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
        Status = mode == ActorAssignmentMode.Morph
            ? "Finding compatible authored actors…"
            : "Finding safe local head and hair materials…";
        try
        {
            inventory = await Task.Run(() => _actorAssignmentService.ReadInventory(
                workspacePath, source.InstancedPath, source.ProfileKey));
        }
        catch (Exception exception)
        {
            AppLog.Error($"Actor-assignment preflight failed for '{source.InstancedPath}'.", exception);
            ErrorMessage = $"Actor assignment could not be prepared: {exception.Message}";
            Status = "Actor-assignment preflight failed; the workspace was not modified.";
            return;
        }
        finally
        {
            IsBusy = false;
        }

        var candidate = _dialogs.ChooseActorAssignment(inventory, mode);
        if (candidate is null)
        {
            Status = "Actor assignment cancelled; the workspace was not modified.";
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        Status = mode == ActorAssignmentMode.Morph
            ? $"Assigning {source.DisplayName} to {candidate.DisplayName}…"
            : $"Assigning material overrides to {candidate.DisplayName}…";
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
                    ErrorMessage = "The morph was assigned, but the refreshed workspace could not be opened.";
                    Status = "Morph assignment completed; workspace reload failed.";
                    return;
                }
                Status = $"Assigned and verified {result.NewMorphPath} on {candidate.DisplayName}; save the package to commit the change.";
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
                    ErrorMessage = "The materials were assigned, but the refreshed workspace could not be opened.";
                    Status = "Material assignment completed; workspace reload failed.";
                    return;
                }
                var changedCount = result.ChangedMaterials.Select(value => value.UIndex).Distinct().Count();
                Status = $"Assigned and verified material overrides on {changedCount} MIC(s); save the package to commit the change.";
                _dialogs.ShowInformation("Materials assigned", BuildMaterialCompletion(result));
                AppLog.Information(
                    $"Actor material assignment verified for '{result.ActorPath}' on {changedCount} local MIC(s).");
            }
        }
        catch (Exception exception)
        {
            ReportContextOperationFailure(
                mode == ActorAssignmentMode.Morph ? "Assign Morph to Actor" : "Assign Materials to Actor",
                mode == ActorAssignmentMode.Morph
                    ? "The morph could not be assigned"
                    : "The materials could not be assigned",
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
            .AppendLine($"Actor: {result.ActorPath}")
            .AppendLine($"Source override: {result.SourceMaterialOverridePath}")
            .AppendLine($"Changed local MICs: {changed.Length}")
            .AppendLine($"Categories replaced: " + string.Join(", ", new[]
            {
                result.ReplacedTextures ? "textures" : null,
                result.ReplacedVectors ? "vectors" : null,
                result.ReplacedScalars ? "scalars" : null
            }.Where(value => value is not null)))
            .AppendLine()
            .AppendLine("Changed:");
        foreach (var target in changed)
        {
            text.AppendLine($"• #{target.UIndex} {target.InstancedPath} ({target.ComponentRole} slot {target.SlotIndex}, {target.Family})");
        }
        text.AppendLine().AppendLine($"Skipped slots: {result.SkippedMaterials.Count}");
        foreach (var skipped in result.SkippedMaterials)
        {
            text.AppendLine($"• #{skipped.UIndex} {skipped.InstancedPath}: {skipped.Reason}");
        }
        text.AppendLine().Append("The change is still in the temporary workspace. Use Save to commit it to the source PCC.");
        return text.ToString();
    }
}
