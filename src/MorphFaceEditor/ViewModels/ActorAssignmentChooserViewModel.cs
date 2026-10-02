using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Windows.Data;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.ViewModels;

public sealed class ActorAssignmentChooserViewModel : ObservableObject
{
    private string _searchText = string.Empty;
    private ActorAssignmentChoice? _selectedChoice;

    public ActorAssignmentChooserViewModel(ActorAssignmentInventory inventory, ActorAssignmentMode mode)
    {
        Inventory = inventory;
        Mode = mode;
        Choices = new ObservableCollection<ActorAssignmentChoice>(inventory.Candidates.Select(candidate =>
            new ActorAssignmentChoice(candidate, mode)));
        FilteredChoices = CollectionViewSource.GetDefaultView(Choices);
        FilteredChoices.Filter = value => value is ActorAssignmentChoice choice &&
                                           (string.IsNullOrWhiteSpace(SearchText) ||
                                            choice.Candidate.SearchText.Contains(
                                                SearchText, StringComparison.OrdinalIgnoreCase));
        FilteredChoices.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ActorAssignmentChoice.GroupName)));
        FilteredChoices.SortDescriptions.Add(new SortDescription(
            nameof(ActorAssignmentChoice.GroupSortOrder), ListSortDirection.Ascending));
        FilteredChoices.SortDescriptions.Add(new SortDescription(
            nameof(ActorAssignmentChoice.ProfileSortOrder), ListSortDirection.Ascending));
        FilteredChoices.SortDescriptions.Add(new SortDescription(
            nameof(ActorAssignmentChoice.DisplayName), ListSortDirection.Ascending));
        FilteredChoices.SortDescriptions.Add(new SortDescription(
            nameof(ActorAssignmentChoice.TechnicalIdentity), ListSortDirection.Ascending));
        SelectedChoice = FilteredChoices.Cast<ActorAssignmentChoice>().FirstOrDefault(choice => choice.IsEligible)
                         ?? FilteredChoices.Cast<ActorAssignmentChoice>().FirstOrDefault();
    }

    public ActorAssignmentInventory Inventory { get; }
    public ActorAssignmentMode Mode { get; }
    public ObservableCollection<ActorAssignmentChoice> Choices { get; }
    public System.ComponentModel.ICollectionView FilteredChoices { get; }
    public string WindowTitle => Mode == ActorAssignmentMode.Morph
        ? Loc.Text("loc_VM_actorAssignment_titleMorph")
        : Loc.Text("loc_VM_actorAssignment_titleMaterials");
    public string Introduction => Mode == ActorAssignmentMode.Morph
        ? Loc.Format("loc_VM_actorAssignment_introMorph", Inventory.SelectedFacePath)
        : Loc.Format("loc_VM_actorAssignment_introMaterials", Inventory.SelectedFacePath);
    public string ConfirmLabel => Mode == ActorAssignmentMode.Morph
        ? Loc.Text("loc_VM_actorAssignment_confirmMorph")
        : Loc.Text("loc_VM_actorAssignment_confirmMaterials");
    public bool CanConfirm => SelectedChoice?.IsEligible == true;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                FilteredChoices.Refresh();
                if (SelectedChoice is not null && !FilteredChoices.Cast<ActorAssignmentChoice>().Contains(SelectedChoice))
                {
                    SelectedChoice = FilteredChoices.Cast<ActorAssignmentChoice>().FirstOrDefault();
                }
            }
        }
    }

    public ActorAssignmentChoice? SelectedChoice
    {
        get => _selectedChoice;
        set
        {
            if (SetProperty(ref _selectedChoice, value))
            {
                OnPropertyChanged(nameof(CanConfirm));
            }
        }
    }
}

public sealed class ActorAssignmentChoice
{
    public ActorAssignmentChoice(ActorAssignmentCandidate candidate, ActorAssignmentMode mode)
    {
        Candidate = candidate;
        Mode = mode;
        Details = BuildDetails(candidate, mode);
    }

    public ActorAssignmentCandidate Candidate { get; }
    public ActorAssignmentMode Mode { get; }
    public string GroupName => Candidate.TargetKind == ActorAssignmentTargetKind.SpawnTemplate
        ? Loc.Text("loc_VM_actorAssignment_groupSpawnTemplates")
        : Loc.Text("loc_VM_actorAssignment_groupPlacedActors");
    public int GroupSortOrder => (int)Candidate.TargetKind;
    public int ProfileSortOrder => string.Equals(
        Candidate.SelectedProfileKey,
        Candidate.HeadMeshProfileKey,
        StringComparison.OrdinalIgnoreCase) ? 0 : 1;
    public string DisplayName => Candidate.DisplayName;
    public string TechnicalIdentity =>
        $"#{Candidate.UIndex}  {Candidate.ClassName}  ·  {ShortLevelPath(Candidate.InstancedPath)}";
    public string EvidenceSummary => Loc.Format(
        "loc_VM_actorAssignment_evidenceSummary",
        Candidate.HeadMeshProfileKey ?? Loc.Text("loc_VM_actorAssignment_unresolved"),
        ShortObjectName(Candidate.MorphTarget?.CurrentMorphPath) ?? Loc.Text("loc_VM_actorAssignment_none"),
        Candidate.SafeMaterialCount);
    public bool IsEligible => Mode == ActorAssignmentMode.Morph
        ? Candidate.CanAssignMorph
        : Candidate.CanAssignMaterials;
    public string EligibilityLabel => IsEligible
        ? Mode == ActorAssignmentMode.Morph
            ? Loc.Text("loc_VM_actorAssignment_morphReady")
            : Candidate.SafeMaterialCount == 1
                ? Loc.Text("loc_VM_actorAssignment_oneMicReady")
                : Loc.Format("loc_VM_actorAssignment_micsReady", Candidate.SafeMaterialCount)
        : Loc.Text("loc_VM_actorAssignment_notEligible");
    public string Details { get; }

    private static string BuildDetails(ActorAssignmentCandidate candidate, ActorAssignmentMode mode)
    {
        var text = new StringBuilder();
        text.AppendLine($"{candidate.DisplayName}");
        text.AppendLine($"#{candidate.UIndex} {candidate.ClassName} · {ShortLevelPath(candidate.InstancedPath)}");
        text.AppendLine();
        text.AppendLine(Loc.Text("loc_VM_actorAssignment_detailsSummaryHeading"));
        text.AppendLine(Loc.Format("loc_VM_actorAssignment_detailsSelectedProfile", candidate.SelectedProfileKey));
        text.AppendLine(Loc.Format("loc_VM_actorAssignment_detailsHeadMesh", ShortObjectName(candidate.HeadMeshPath) ?? Loc.Text("loc_VM_actorAssignment_unresolved")));
        text.AppendLine(Loc.Format("loc_VM_actorAssignment_detailsHeadProfile", candidate.HeadMeshProfileKey ?? Loc.Text("loc_VM_actorAssignment_unresolved")));
        if (mode == ActorAssignmentMode.Morph)
        {
            text.AppendLine(Loc.Format("loc_VM_actorAssignment_detailsMorph", candidate.CanAssignMorph
                ? Loc.Text("loc_VM_actorAssignment_eligible")
                : CompactMorphReason(candidate)));
        }
        else
        {
            text.AppendLine(Loc.Format("loc_VM_actorAssignment_detailsMaterials", candidate.CanAssignMaterials
                ? candidate.SafeMaterialCount == 1
                    ? Loc.Text("loc_VM_actorAssignment_oneSafeLocalMic")
                    : Loc.Format("loc_VM_actorAssignment_safeLocalMics", candidate.SafeMaterialCount)
                : candidate.MaterialIneligibilityReason));
        }

        if (mode == ActorAssignmentMode.Morph && candidate.MorphTarget is not null)
        {
            text.AppendLine();
            text.AppendLine(Loc.Text("loc_VM_actorAssignment_exactMorphOwnerHeading"));
            text.AppendLine($"• #{candidate.MorphTarget.OwnerUIndex} {candidate.MorphTarget.OwnerClass} · " +
                            $"{ShortLevelPath(candidate.MorphTarget.OwnerPath)}.{candidate.MorphTarget.PropertyName}");
            text.AppendLine(Loc.Format("loc_VM_actorAssignment_currentMorph",
                ShortObjectName(candidate.MorphTarget.CurrentMorphPath) ?? Loc.Text("loc_VM_actorAssignment_none")));
        }

        if (mode == ActorAssignmentMode.Materials)
        {
            text.AppendLine();
            text.AppendLine(Loc.Text("loc_VM_actorAssignment_safeMaterialTargetsHeading"));
            if (candidate.MaterialTargets.Count == 0)
            {
                text.AppendLine(Loc.Format("loc_VM_actorAssignment_noneBullet", Loc.Text("loc_VM_actorAssignment_none")));
            }
            else
            {
                text.AppendLine();
                AppendMaterialTargets(text, candidate.MaterialTargets);
            }

            text.AppendLine();
            text.AppendLine(Loc.Text("loc_VM_actorAssignment_skippedMaterialsHeading"));
            if (candidate.SkippedMaterials.Count == 0)
            {
                text.AppendLine(Loc.Format("loc_VM_actorAssignment_noneBullet", Loc.Text("loc_VM_actorAssignment_none")));
            }
            else
            {
                text.AppendLine();
                AppendSkippedMaterials(text, candidate.SkippedMaterials);
            }
        }

        if (candidate.ReferencedBy.Count > 0)
        {
            text.AppendLine();
            text.AppendLine(Loc.Text("loc_VM_actorAssignment_localReferrersHeading"));
            foreach (var reference in candidate.ReferencedBy)
            {
                text.AppendLine($"• #{reference.UIndex} {ShortLevelPath(reference.InstancedPath)} · {reference.PropertyPath}");
            }
        }
        if (candidate.Warnings.Count > 0)
        {
            text.AppendLine();
            text.AppendLine(Loc.Text("loc_VM_actorAssignment_warningsHeading"));
            foreach (var warning in candidate.Warnings) text.AppendLine($"• {warning}");
        }
        if (!(mode == ActorAssignmentMode.Morph ? candidate.CanAssignMorph : candidate.CanAssignMaterials))
        {
            text.AppendLine();
            text.AppendLine(Loc.Text("loc_VM_actorAssignment_rowCannotConfirm"));
        }
        return text.ToString().TrimEnd();
    }

    internal static string ShortLevelPath(string path)
    {
        const string prefix = "TheWorld.PersistentLevel.";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? path[prefix.Length..]
            : path;
    }

    internal static string? ShortObjectName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var shortened = ShortLevelPath(path);
        var separator = shortened.LastIndexOf('.');
        return separator >= 0 ? shortened[(separator + 1)..] : shortened;
    }

    private static void AppendMaterialTargets(
        StringBuilder text,
        IEnumerable<ActorAssignmentMaterialTarget> targets)
    {
        var roles = targets.GroupBy(value => value.ComponentRole).OrderBy(value => value.Key).ToArray();
        for (var roleIndex = 0; roleIndex < roles.Length; roleIndex++)
        {
            if (roleIndex > 0) text.AppendLine();
            text.AppendLine(LocalizedComponentRole(roles[roleIndex].Key));
            var roleTargets = roles[roleIndex].OrderBy(value => value.SlotIndex).ToArray();
            for (var targetIndex = 0; targetIndex < roleTargets.Length; targetIndex++)
            {
                if (targetIndex > 0) text.AppendLine();
                var target = roleTargets[targetIndex];
                text.AppendLine(Loc.Format("loc_VM_actorAssignment_slotFamily", target.SlotIndex, target.Family));
                AppendCompactChain(text, target.ObjectName, target.ParentChain);
            }
        }
    }

    private static void AppendSkippedMaterials(
        StringBuilder text,
        IEnumerable<ActorAssignmentSkippedMaterial> skippedMaterials)
    {
        var roles = skippedMaterials.GroupBy(value => value.ComponentRole).OrderBy(value => value.Key).ToArray();
        for (var roleIndex = 0; roleIndex < roles.Length; roleIndex++)
        {
            if (roleIndex > 0) text.AppendLine();
            text.AppendLine(LocalizedComponentRole(roles[roleIndex].Key));
            var roleTargets = roles[roleIndex].OrderBy(value => value.SlotIndex).ToArray();
            for (var targetIndex = 0; targetIndex < roleTargets.Length; targetIndex++)
            {
                if (targetIndex > 0) text.AppendLine();
                var skipped = roleTargets[targetIndex];
                text.AppendLine(Loc.Format("loc_VM_actorAssignment_slotFamily", skipped.SlotIndex, skipped.Family));
                AppendCompactChain(text, skipped.ObjectName, skipped.ParentChain);
                text.AppendLine(Loc.Format("loc_VM_actorAssignment_skippedReason", skipped.Reason));
            }
        }
    }

    private static void AppendCompactChain(
        StringBuilder text,
        string fallbackObjectName,
        IReadOnlyList<ActorMaterialChainEntry> chain)
    {
        var localName = chain.Count == 0
            ? fallbackObjectName
            : ShortObjectName(chain[0].InstancedPath) ?? fallbackObjectName;
        text.AppendLine(localName);
        var parents = chain.Skip(1)
            .Where(value => !IsTransientMaterialUser(value))
            .Select(value => ShortObjectName(value.InstancedPath))
            .Where(value => !string.IsNullOrWhiteSpace(value));
        var parentChain = string.Join(" → ", parents);
        if (!string.IsNullOrWhiteSpace(parentChain)) text.AppendLine(parentChain);
    }

    private static bool IsTransientMaterialUser(ActorMaterialChainEntry value)
    {
        var name = ShortObjectName(value.InstancedPath);
        return value.ClassName.Equals("RvrEffectsMaterialUser", StringComparison.OrdinalIgnoreCase) ||
               name?.EndsWith("_USER", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string LocalizedComponentRole(ActorComponentRole role) => role switch
    {
        ActorComponentRole.Head => Loc.Text("loc_VM_actorAssignment_componentRoleHead"),
        ActorComponentRole.Hair => Loc.Text("loc_VM_actorAssignment_componentRoleHair"),
        _ => Loc.Text("loc_VM_actorAssignment_componentRoleOther")
    };

    private static string CompactMorphReason(ActorAssignmentCandidate candidate)
    {
        var reason = candidate.MorphIneligibilityReason ?? Loc.Text("loc_VM_actorAssignment_notEligibleLowercase");
        return string.IsNullOrWhiteSpace(candidate.HeadMeshPath)
            ? reason
            : reason.Replace(
                candidate.HeadMeshPath,
                ShortObjectName(candidate.HeadMeshPath),
                StringComparison.OrdinalIgnoreCase);
    }
}
