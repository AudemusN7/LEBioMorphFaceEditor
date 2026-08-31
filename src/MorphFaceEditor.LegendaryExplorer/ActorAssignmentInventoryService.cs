using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.LegendaryExplorer;

/// <summary>
/// Inventories supported actor targets and safe local MIC slots in a workspace package.
/// This service is read-only: every returned value is detached from Legendary Explorer objects.
/// </summary>
public sealed partial class ActorAssignmentInventoryService
{
    private static readonly string[] HeadComponentProperties =
        ["m_oHeadMesh", "HeadMesh", "Mesh", "SkeletalMeshComponent"];
    private static readonly string[] HairComponentProperties = ["m_oHairMesh", "HairMesh"];
    private readonly Func<MorphFaceGame, int, string?> _gameNameResolver;

    public ActorAssignmentInventoryService(Func<MorphFaceGame, int, string?>? gameNameResolver = null)
    {
        var installedResolver = gameNameResolver is null ? new InstalledGameNameResolver() : null;
        _gameNameResolver = gameNameResolver ?? installedResolver!.Resolve;
    }

    public ActorAssignmentInventory Read(
        string packagePath,
        string selectedFaceSelector,
        string selectedProfileKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedFaceSelector);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedProfileKey);
        LegendaryExplorerCoreRuntime.Initialize();

        var fullPath = Path.GetFullPath(packagePath);
        using var cache = new PackageCache { CacheMaxSize = 12 };
        var package = cache.GetCachedPackage(fullPath)
            ?? throw new InvalidDataException($"Legendary Explorer Core could not open '{fullPath}'.");
        var game = ToMorphFaceGame(package.Game);
        if (game == MorphFaceGame.Unsupported)
        {
            throw new NotSupportedException($"Actor assignment does not support {package.Game} packages.");
        }
        var expectedPrefix = game.ToString().ToLowerInvariant() + "-";
        if (!selectedProfileKey.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Selected profile '{selectedProfileKey}' does not belong to the {game} workspace.");
        }

        var face = ExportSelector.Find(package, selectedFaceSelector, "BioMorphFace");
        var resolver = new GamePackageReferenceResolver(cache);
        var candidates = package.Exports
            .Where(export => !export.IsDefaultObject &&
                             !export.ObjectNameString.StartsWith("Default__", StringComparison.OrdinalIgnoreCase))
            .Select(export => (Export: export, Kind: GetTargetKind(export, game)))
            .Where(item => item.Kind is not null)
            .Select(item => BuildCandidate(
                package, item.Export, item.Kind!.Value, game, selectedProfileKey, resolver, cache))
            .OrderBy(candidate => candidate.TargetKind)
            .ThenBy(candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.InstancedPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ActorAssignmentInventory(
            game,
            face.UIndex,
            face.InstancedFullPath,
            selectedProfileKey,
            candidates);
    }

    private ActorAssignmentCandidate BuildCandidate(
        IMEPackage package,
        ExportEntry actor,
        ActorAssignmentTargetKind targetKind,
        MorphFaceGame game,
        string selectedProfileKey,
        GamePackageReferenceResolver resolver,
        PackageCache cache)
    {
        var warnings = new List<string>();
        var behavior = ResolveRelated(actor, ["m_oBehavior", "oBioComponent"], resolver, cache)?.Value;
        var actorType = ResolveRelated(actor, ["ActorType"], resolver, cache)?.Value
                        ?? (behavior is null
                            ? null
                            : ResolveRelated(behavior, ["m_oActorType", "ActorType"], resolver, cache)?.Value);
        if (targetKind == ActorAssignmentTargetKind.SpawnTemplate)
        {
            actorType = actor;
        }

        var evidence = ReadIdentityEvidence(actor, behavior, actorType, resolver, cache, game);
        var displayEvidence = evidence.FirstOrDefault(value => value.Kind is
            ActorIdentityEvidenceKind.LocalTag or ActorIdentityEvidenceKind.LocalUniqueTag)
            ?? evidence.FirstOrDefault(value => value.Kind is
                ActorIdentityEvidenceKind.InheritedTag or ActorIdentityEvidenceKind.InheritedUniqueTag)
            ?? evidence.FirstOrDefault(value => value.Kind == ActorIdentityEvidenceKind.GameName)
            ?? evidence.FirstOrDefault(value => value.Kind == ActorIdentityEvidenceKind.ActorType);
        var displayName = displayEvidence is null
            ? actor.ObjectNameString
            : displayEvidence.Kind is ActorIdentityEvidenceKind.InheritedTag or
                ActorIdentityEvidenceKind.InheritedUniqueTag
                ? $"{displayEvidence.Value} (inherited)"
                : displayEvidence.Value;

        var components = ResolveComponents(actor, resolver, cache, warnings);
        var headComponent = components.FirstOrDefault(value => value.Role == ActorComponentRole.Head);
        var morphTarget = ResolveMorphTarget(actor, targetKind, game, behavior, resolver, cache, warnings);
        // Type-only LE1 factory targets legitimately lack placed components. Their existing
        // morph's base head is bounded local evidence; placed pawns never receive this fallback.
        var headMeshPath = headComponent?.SkeletalMeshPath ??
                           (targetKind == ActorAssignmentTargetKind.SpawnTemplate
                               ? morphTarget?.CurrentMorphBaseHeadPath
                               : null);
        var headSuffix = HeadProfileIdentity.InferSuffix(headMeshPath);
        var gamePrefix = game.ToString().ToLowerInvariant();
        var headProfileKey = headSuffix is null ? null : HeadProfileIdentity.WithGame(gamePrefix, headSuffix);

        var morphReason = MorphIneligibilityReason(
            targetKind, game, selectedProfileKey, headMeshPath, headProfileKey, morphTarget);

        var eligibleMaterials = new List<ActorAssignmentMaterialTarget>();
        var skippedMaterials = new List<ActorAssignmentSkippedMaterial>();
        foreach (var component in components)
        {
            InventoryComponentMaterials(
                package, component, gamePrefix, selectedProfileKey, headProfileKey,
                resolver, cache, eligibleMaterials, skippedMaterials, warnings);
        }
        eligibleMaterials = eligibleMaterials
            .OrderBy(value => value.ComponentRole)
            .ThenBy(value => value.ComponentPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.SlotIndex)
            .ThenBy(value => value.InstancedPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        skippedMaterials = skippedMaterials
            .OrderBy(value => value.ComponentRole)
            .ThenBy(value => value.ComponentPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.SlotIndex)
            .ThenBy(value => value.InstancedPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (components.Any(component => component.Inherited))
        {
            warnings.Add("One or more head/hair components are inherited; the chooser should expose their exact owner and slots.");
        }
        if (skippedMaterials.Any(material => material.Reason.Contains("import", StringComparison.OrdinalIgnoreCase)))
        {
            warnings.Add("Imported materials were inspected for classification but are never writable targets.");
        }
        if (eligibleMaterials.Count == 0)
        {
            warnings.Add("No safe compatible local head or hair MICs were found.");
        }

        var referencedBy = targetKind == ActorAssignmentTargetKind.SpawnTemplate
            ? FindReferrers(package, actor)
            : [];
        if (targetKind == ActorAssignmentTargetKind.SpawnTemplate)
        {
            warnings.Add(referencedBy.Count == 0
                ? "This spawn-template assignment can affect every actor created from the type."
                : $"This spawn-template assignment can affect every actor created from the type; {referencedBy.Count} local referrer(s) were found.");
        }

        var searchValues = evidence.Select(value => value.Value)
            .Concat(evidence.Select(value => value.SourcePath))
            .Concat([
                actor.ObjectNameString,
                actor.ClassName,
                actor.InstancedFullPath,
                actor.UIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                $"#{actor.UIndex}",
                headMeshPath ?? string.Empty,
                morphTarget?.CurrentMorphPath ?? string.Empty
            ])
            .Concat(EnumerateArchetypeChain(actor, resolver).SelectMany(value => new[]
            {
                value.Export.ObjectNameString,
                value.Export.ClassName,
                value.Export.UIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                value.Export.InstancedFullPath
            }))
            .Concat(new[] { behavior, actorType }.Where(value => value is not null).Cast<ExportEntry>()
                .SelectMany(value => new[]
                {
                    value.ObjectNameString,
                    value.ClassName,
                    value.UIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    value.InstancedFullPath
                }))
            .Concat(referencedBy.SelectMany(reference => new[]
                { reference.ClassName, reference.InstancedPath, reference.UIndex.ToString() }))
            .Where(value => !string.IsNullOrWhiteSpace(value));

        return new ActorAssignmentCandidate(
            actor.UIndex,
            actor.ClassName,
            actor.ObjectNameString,
            actor.InstancedFullPath,
            targetKind,
            displayName,
            evidence,
            string.Join('\n', searchValues.Distinct(StringComparer.OrdinalIgnoreCase)),
            selectedProfileKey,
            headMeshPath,
            headProfileKey,
            morphReason is null,
            morphReason,
            morphTarget,
            eligibleMaterials.Count > 0,
            eligibleMaterials.Count > 0 ? null : "No safe compatible local head or hair MICs were found.",
            components,
            eligibleMaterials,
            skippedMaterials,
            referencedBy,
            warnings.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static ActorAssignmentTargetKind? GetTargetKind(ExportEntry export, MorphFaceGame game)
    {
        if (game == MorphFaceGame.LE1)
        {
            if (export.IsA("BioPawnChallengeScaledType")) return ActorAssignmentTargetKind.SpawnTemplate;
            if (export.IsA("BioPawn")) return ActorAssignmentTargetKind.PlacedActor;
            return null;
        }
        if (game == MorphFaceGame.LE2 &&
            (export.IsA("BioPawn") || export.IsA("SFXSkeletalMeshActor") ||
             export.ClassName.Equals("SFXSkeletalMeshActorMAT", StringComparison.OrdinalIgnoreCase)))
        {
            return ActorAssignmentTargetKind.PlacedActor;
        }
        if (game == MorphFaceGame.LE3 &&
            (export.IsA("BioPawn") || export.IsA("SFXStuntActor") || export.IsA("SFXSkeletalMeshActor") ||
             export.ClassName.Equals("SFXSkeletalMeshActorMAT", StringComparison.OrdinalIgnoreCase)))
        {
            return ActorAssignmentTargetKind.PlacedActor;
        }
        return null;
    }

    private IReadOnlyList<ActorIdentityEvidence> ReadIdentityEvidence(
        ExportEntry actor,
        ExportEntry? behavior,
        ExportEntry? actorType,
        GamePackageReferenceResolver resolver,
        PackageCache cache,
        MorphFaceGame game)
    {
        var result = new List<ActorIdentityEvidence>();
        var actorChain = EnumerateArchetypeChain(actor, resolver).ToArray();
        foreach (var (source, inherited) in actorChain)
        {
            AddTagEvidence(result, source, inherited, cache);
            if (inherited)
            {
                result.Add(new ActorIdentityEvidence(
                    ActorIdentityEvidenceKind.Archetype,
                    "Archetype",
                    source.ObjectNameString,
                    source.InstancedFullPath));
            }
        }
        foreach (var related in new[] { behavior, actorType }.Where(value => value is not null).Cast<ExportEntry>())
        {
            var isActorType = related == actorType;
            AddTagEvidence(
                result,
                related,
                inherited: false,
                cache,
                isActorType ? "Actor type" : "Behavior",
                isActorType ? ActorIdentityEvidenceKind.ActorTypeTag : ActorIdentityEvidenceKind.BehaviorTag);
        }

        if (actorType is not null)
        {
            var stringRef = FindProperty<StringRefProperty>(actorType, "ActorGameNameStrRef", resolver, cache)?.Property.Value
                            ?? FindProperty<IntProperty>(actorType, "ActorGameNameStrRef", resolver, cache)?.Property.Value
                            ?? 0;
            if (stringRef > 0)
            {
                var gameName = _gameNameResolver(game, stringRef);
                if (!string.IsNullOrWhiteSpace(gameName))
                {
                    result.Add(new ActorIdentityEvidence(
                        ActorIdentityEvidenceKind.GameName,
                        $"Game name (StrRef {stringRef})",
                        gameName,
                        actorType.InstancedFullPath));
                }
                else
                {
                    result.Add(new ActorIdentityEvidence(
                        ActorIdentityEvidenceKind.GameNameStringRef,
                        "Game-name string reference",
                        stringRef.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        actorType.InstancedFullPath));
                }
            }
            result.Add(new ActorIdentityEvidence(
                ActorIdentityEvidenceKind.ActorType,
                "Actor type",
                actorType.ObjectNameString,
                actorType.InstancedFullPath));
        }
        result.Add(new ActorIdentityEvidence(
            ActorIdentityEvidenceKind.Export,
            "Export",
            actor.ObjectNameString,
            actor.InstancedFullPath));
        return result
            .DistinctBy(value => (value.Kind, value.Value, value.SourcePath))
            .ToArray();
    }

    private static void AddTagEvidence(
        ICollection<ActorIdentityEvidence> result,
        ExportEntry source,
        bool inherited,
        PackageCache cache,
        string? sourceLabel = null,
        ActorIdentityEvidenceKind? relatedKind = null)
    {
        foreach (var property in source.GetProperties(packageCache: cache))
        {
            var propertyName = property.Name.Name;
            if (!propertyName.Contains("Tag", StringComparison.OrdinalIgnoreCase)) continue;
            var value = property switch
            {
                NameProperty name => name.Value.Instanced,
                StrProperty text => text.Value,
                _ => null
            };
            if (string.IsNullOrWhiteSpace(value) || value.Equals("None", StringComparison.OrdinalIgnoreCase)) continue;

            var ordinaryTag = propertyName.Equals("Tag", StringComparison.OrdinalIgnoreCase);
            var kind = relatedKind ?? (inherited, ordinaryTag) switch
            {
                (false, true) => ActorIdentityEvidenceKind.LocalTag,
                (false, false) => ActorIdentityEvidenceKind.LocalUniqueTag,
                (true, true) => ActorIdentityEvidenceKind.InheritedTag,
                _ => ActorIdentityEvidenceKind.InheritedUniqueTag
            };
            var labelPrefix = sourceLabel is null ? string.Empty : sourceLabel + " ";
            result.Add(new ActorIdentityEvidence(
                kind,
                $"{labelPrefix}{(inherited ? "inherited " : string.Empty)}{propertyName}",
                value,
                source.InstancedFullPath));
        }
    }

    private static IReadOnlyList<ActorAssignmentComponent> ResolveComponents(
        ExportEntry actor,
        GamePackageReferenceResolver resolver,
        PackageCache cache,
        ICollection<string> warnings)
    {
        var result = new List<ActorAssignmentComponent>();
        AddComponent(ActorComponentRole.Head, HeadComponentProperties);
        AddComponent(ActorComponentRole.Hair, HairComponentProperties);
        return result.DistinctBy(component => (component.Role, component.UIndex)).ToArray();

        void AddComponent(ActorComponentRole role, IReadOnlyList<string> propertyNames)
        {
            foreach (var propertyName in propertyNames)
            {
                var reference = FindProperty<ObjectProperty>(actor, propertyName, resolver, cache);
                if (reference is null || reference.Property.Value == 0) continue;
                var entry = reference.Property.ResolveToEntry(reference.Owner.FileRef);
                var component = resolver.Resolve(entry);
                if (component is null)
                {
                    warnings.Add($"{propertyName} on '{actor.InstancedFullPath}' could not be resolved.");
                    continue;
                }
                if (!component.IsA("SkeletalMeshComponent"))
                {
                    warnings.Add($"{propertyName} resolved to {component.ClassName}, not a skeletal component.");
                    continue;
                }

                var meshReference = FindProperty<ObjectProperty>(component, "SkeletalMesh", resolver, cache);
                var rawMesh = meshReference?.Property.ResolveToEntry(meshReference.Owner.FileRef);
                var mesh = resolver.Resolve(rawMesh);
                result.Add(new ActorAssignmentComponent(
                    role,
                    component.UIndex,
                    component.ObjectNameString,
                    component.InstancedFullPath,
                    component.FileRef.FilePath,
                    ReferenceEquals(component.FileRef, actor.FileRef),
                    mesh?.InstancedFullPath ?? rawMesh?.InstancedFullPath,
                    reference.Inherited));
                break;
            }
        }
    }

    private static ActorAssignmentMorphTarget? ResolveMorphTarget(
        ExportEntry actor,
        ActorAssignmentTargetKind targetKind,
        MorphFaceGame game,
        ExportEntry? behavior,
        GamePackageReferenceResolver resolver,
        PackageCache cache,
        ICollection<string> warnings)
    {
        ExportEntry? owner;
        string propertyName;
        if (game == MorphFaceGame.LE1 && targetKind == ActorAssignmentTargetKind.PlacedActor)
        {
            var appearanceReference = behavior is null
                ? null
                : FindProperty<ObjectProperty>(behavior, "m_oAppearanceType", resolver, cache);
            var appearanceEntry = appearanceReference?.Property.ResolveToEntry(appearanceReference.Owner.FileRef);
            owner = appearanceEntry as ExportEntry;
            propertyName = "m_oMorphFace";
            if (owner is null || !ReferenceEquals(owner.FileRef, actor.FileRef) ||
                !owner.IsA("BioInterface_Appearance_Pawn"))
            {
                warnings.Add("The LE1 pawn has no local BioInterface_Appearance_Pawn; its shared actor type will not be used as a fallback.");
                return null;
            }
        }
        else
        {
            owner = actor;
            propertyName = game == MorphFaceGame.LE1 ? "m_oMorphFace" : "MorphHead";
        }

        var currentProperty = owner.GetProperty<ObjectProperty>(propertyName);
        var currentEntry = currentProperty?.ResolveToEntry(owner.FileRef);
        var currentResolved = resolver.Resolve(currentEntry);
        var baseHeadReference = currentResolved is { ClassName: "BioMorphFace" }
            ? FindProperty<ObjectProperty>(currentResolved, "m_oBaseHead", resolver, cache)
            : null;
        var rawBaseHead = baseHeadReference?.Property.ResolveToEntry(baseHeadReference.Owner.FileRef);
        var resolvedBaseHead = resolver.Resolve(rawBaseHead);
        return new ActorAssignmentMorphTarget(
            owner.UIndex,
            owner.ClassName,
            owner.InstancedFullPath,
            propertyName,
            currentEntry?.UIndex ?? 0,
            currentResolved?.InstancedFullPath ?? currentEntry?.InstancedFullPath,
            resolvedBaseHead?.InstancedFullPath ?? rawBaseHead?.InstancedFullPath);
    }

    private static string? MorphIneligibilityReason(
        ActorAssignmentTargetKind targetKind,
        MorphFaceGame game,
        string selectedProfileKey,
        string? headMeshPath,
        string? headProfileKey,
        ActorAssignmentMorphTarget? morphTarget)
    {
        if (morphTarget is null)
        {
            return game == MorphFaceGame.LE1 && targetKind == ActorAssignmentTargetKind.PlacedActor
                ? "No local LE1 appearance interface owns m_oMorphFace; shared actor types are not an implicit fallback."
                : "No safe local morph-property owner was resolved.";
        }
        if (string.IsNullOrWhiteSpace(headMeshPath))
        {
            return "No head skeletal mesh could be resolved, so morph compatibility is unknown.";
        }
        if (headProfileKey is null)
        {
            return $"Head mesh '{headMeshPath}' is not a recognized morph profile.";
        }
        return HeadProfileIdentity.IsGeometryCompatible(selectedProfileKey, headProfileKey)
            ? null
            : $"Head mesh profile '{headProfileKey}' is incompatible with selected face profile '{selectedProfileKey}'.";
    }

}
