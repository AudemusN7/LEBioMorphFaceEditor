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
        ? "Assign Morph to Actor"
        : "Assign Materials to Actor";
    public string Introduction => Mode == ActorAssignmentMode.Morph
        ? $"Choose the authored actor that should use {Inventory.SelectedFacePath}. The exact morph owner is shown before anything is changed."
        : $"Choose an actor whose safe local head/hair MICs should receive the material overrides from {Inventory.SelectedFacePath}.";
    public string ConfirmLabel => Mode == ActorAssignmentMode.Morph ? "Assign Morph" : "Assign Materials";
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
        ? "SPAWN TEMPLATES / ACTOR TYPES"
        : "PLACED ACTORS";
    public int GroupSortOrder => (int)Candidate.TargetKind;
    public int ProfileSortOrder => string.Equals(
        Candidate.SelectedProfileKey,
        Candidate.HeadMeshProfileKey,
        StringComparison.OrdinalIgnoreCase) ? 0 : 1;
    public string DisplayName => Candidate.DisplayName;
    public string TechnicalIdentity =>
        $"#{Candidate.UIndex}  {Candidate.ClassName}  ·  {ShortLevelPath(Candidate.InstancedPath)}";
    public string EvidenceSummary =>
        $"Head: {Candidate.HeadMeshProfileKey ?? "unresolved"}  ·  " +
        $"Morph: {ShortObjectName(Candidate.MorphTarget?.CurrentMorphPath) ?? "none"}  ·  " +
        $"Safe MICs: {Candidate.SafeMaterialCount}";
    public bool IsEligible => Mode == ActorAssignmentMode.Morph
        ? Candidate.CanAssignMorph
        : Candidate.CanAssignMaterials;
    public string EligibilityLabel => IsEligible
        ? Mode == ActorAssignmentMode.Morph ? "MORPH READY" : $"{Candidate.SafeMaterialCount} MIC(S) READY"
        : "NOT ELIGIBLE";
    public string Details { get; }

    private static string BuildDetails(ActorAssignmentCandidate candidate, ActorAssignmentMode mode)
    {
        var text = new StringBuilder();
        text.AppendLine($"{candidate.DisplayName}");
        text.AppendLine($"#{candidate.UIndex} {candidate.ClassName} · {ShortLevelPath(candidate.InstancedPath)}");
        text.AppendLine();
        text.AppendLine("ASSIGNMENT SUMMARY");
        text.AppendLine($"• Selected profile: {candidate.SelectedProfileKey}");
        text.AppendLine($"• Head mesh: {ShortObjectName(candidate.HeadMeshPath) ?? "unresolved"}");
        text.AppendLine($"• Head profile: {candidate.HeadMeshProfileKey ?? "unresolved"}");
        if (mode == ActorAssignmentMode.Morph)
        {
            text.AppendLine($"• Morph: {(candidate.CanAssignMorph ? "eligible" : CompactMorphReason(candidate))}");
        }
        else
        {
            text.AppendLine($"• Materials: {(candidate.CanAssignMaterials ? $"{candidate.SafeMaterialCount} safe local MIC(s)" : candidate.MaterialIneligibilityReason)}");
        }

        if (mode == ActorAssignmentMode.Morph && candidate.MorphTarget is not null)
        {
            text.AppendLine();
            text.AppendLine("EXACT MORPH OWNER");
            text.AppendLine($"• #{candidate.MorphTarget.OwnerUIndex} {candidate.MorphTarget.OwnerClass} · " +
                            $"{ShortLevelPath(candidate.MorphTarget.OwnerPath)}.{candidate.MorphTarget.PropertyName}");
            text.AppendLine($"• Current: {ShortObjectName(candidate.MorphTarget.CurrentMorphPath) ?? "none"}");
        }

        if (mode == ActorAssignmentMode.Materials)
        {
            text.AppendLine();
            text.AppendLine("SAFE MATERIAL TARGETS");
            if (candidate.MaterialTargets.Count == 0)
            {
                text.AppendLine("• None");
            }
            else
            {
                text.AppendLine();
                AppendMaterialTargets(text, candidate.MaterialTargets);
            }

            text.AppendLine();
            text.AppendLine("SKIPPED MATERIALS");
            if (candidate.SkippedMaterials.Count == 0)
            {
                text.AppendLine("• None");
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
            text.AppendLine("LOCAL REFERRERS");
            foreach (var reference in candidate.ReferencedBy)
            {
                text.AppendLine($"• #{reference.UIndex} {ShortLevelPath(reference.InstancedPath)} · {reference.PropertyPath}");
            }
        }
        if (candidate.Warnings.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("WARNINGS");
            foreach (var warning in candidate.Warnings) text.AppendLine($"• {warning}");
        }
        if (!(mode == ActorAssignmentMode.Morph ? candidate.CanAssignMorph : candidate.CanAssignMaterials))
        {
            text.AppendLine();
            text.AppendLine("This row cannot be confirmed for the current operation.");
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
            text.AppendLine(roles[roleIndex].Key.ToString());
            var roleTargets = roles[roleIndex].OrderBy(value => value.SlotIndex).ToArray();
            for (var targetIndex = 0; targetIndex < roleTargets.Length; targetIndex++)
            {
                if (targetIndex > 0) text.AppendLine();
                var target = roleTargets[targetIndex];
                text.AppendLine($"Slot {target.SlotIndex} · {target.Family}");
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
            text.AppendLine(roles[roleIndex].Key.ToString());
            var roleTargets = roles[roleIndex].OrderBy(value => value.SlotIndex).ToArray();
            for (var targetIndex = 0; targetIndex < roleTargets.Length; targetIndex++)
            {
                if (targetIndex > 0) text.AppendLine();
                var skipped = roleTargets[targetIndex];
                text.AppendLine($"Slot {skipped.SlotIndex} · {skipped.Family}");
                AppendCompactChain(text, skipped.ObjectName, skipped.ParentChain);
                text.AppendLine($"Skipped: {skipped.Reason}");
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

    private static string CompactMorphReason(ActorAssignmentCandidate candidate)
    {
        var reason = candidate.MorphIneligibilityReason ?? "not eligible";
        return string.IsNullOrWhiteSpace(candidate.HeadMeshPath)
            ? reason
            : reason.Replace(
                candidate.HeadMeshPath,
                ShortObjectName(candidate.HeadMeshPath),
                StringComparison.OrdinalIgnoreCase);
    }
}
