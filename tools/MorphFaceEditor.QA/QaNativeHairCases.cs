using System.Numerics;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;
using MorphFaceEditor.ViewModels;

namespace MorphFaceEditor.QA;

internal static partial class QaCases
{
    private sealed record NativeHairSource(
        string SourceKey,
        string SexFamily,
        string MeshPath,
        string MasterMaterialName,
        HumanMaterialPreviewModel PreviewModel);

    /// <summary>
    /// Exercises the native face workflow with the exact official hair mesh and MIC
    /// selected by the Custom material catalogue source key. The face seed is fixed
    /// and only provides a valid ordinary BioMorphFace owner for the attachment.
    /// </summary>
    private static async Task NativeHairMaterials(QaContext c, string sourceKey)
    {
        var game = c.Face.Game;
        var hair = ResolveNativeHairSource(game, sourceKey);
        QaContext.Require(c.Face.Family.Equals(hair.SexFamily, StringComparison.OrdinalIgnoreCase),
            $"Native hair source '{sourceKey}' requires the {hair.SexFamily} fixture, but this case uses {c.Face.Family}.");

        var registry = await c.Textures.ReadAsync(game);
        if (!registry.IsAvailable)
            throw new QaPrerequisiteException(
                $"{game} asset database must be Ready and current before validating native hair. {registry.RegistryStatus.ErrorMessage}");

        var sexPrefix = hair.SexFamily == "HMM" ? "BIOG_HMM_HIR" : "BIOG_HMF_HIR";
        var candidate = registry.AttachmentMeshes.SingleOrDefault(value =>
            value.CanonicalPath.Equals(hair.MeshPath, StringComparison.OrdinalIgnoreCase) &&
            value.CanonicalPath.StartsWith(sexPrefix, StringComparison.OrdinalIgnoreCase) &&
            value.IsEligibleOfficialDefault && value.EffectiveOccurrence.Origin is
                TextureCatalogOrigin.BaseGame or TextureCatalogOrigin.OfficialDlc)
            ?? throw new InvalidDataException(
                $"The official {game} attachment registry has no eligible {hair.SexFamily} mesh '{hair.MeshPath}' for '{sourceKey}'.");
        var meshIdentity = new AssetIdentity(candidate.EffectiveOccurrence.PackagePath,
            candidate.CanonicalPath, candidate.EffectiveOccurrence.ExportUIndex, "SkeletalMesh");
        c.TrackInput(meshIdentity.PackagePath);

        var nativeAttachment = c.MeasureSync($"Read native hair evidence · {sourceKey}", () =>
        {
            using var reader = new MorphFacePackageReader();
            return reader.LoadDetachedAttachment(meshIdentity.PackagePath, meshIdentity.InstancedPath);
        }, QaStageKind.Load);
        var nativeRenderData = nativeAttachment.Mesh.RenderData;
        QaContext.Require(nativeRenderData is { MaterialSlots.Count: > 0 },
            $"Exact native hair mesh '{hair.MeshPath}' has no resolved material slots.");
        var nativeHairMatches = nativeAttachment.Materials.Materials.Values
            .Where(material => material.Family == HeadMaterialFamily.Hair &&
                material.MasterMaterialName.Equals(hair.MasterMaterialName, StringComparison.OrdinalIgnoreCase) &&
                material.PreviewModel == hair.PreviewModel)
            .OrderBy(material => material.Source.InstancedPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        QaContext.Require(nativeHairMatches.Length == 1,
            $"Exact native hair mesh '{hair.MeshPath}' must resolve one {hair.MasterMaterialName} " +
            $"graph with preview model {hair.PreviewModel}; found {nativeHairMatches.Length}. " +
            "Resolved slots: " + string.Join(" | ", nativeAttachment.Materials.Materials.Values.Select(value =>
                $"{value.Source.InstancedPath} -> {value.MasterMaterialName} ({value.PreviewModel})")));
        var sourceHairMaterial = nativeHairMatches[0];
        QaContext.Require(nativeRenderData!.MaterialSlots.Any(slot => slot is not null &&
                slot.InstancedPath.Equals(sourceHairMaterial.Source.InstancedPath, StringComparison.OrdinalIgnoreCase)),
            $"Native hair MIC '{sourceHairMaterial.Source.InstancedPath}' is not referenced by '{hair.MeshPath}'.");

        var corpus = c.CorpusPackage(game);
        var seedFace = QaCoverageSelection.ExcludePaths(
                MorphFaceReferenceInspector.Inspect(corpus), value => value.FacePath,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase))
            .Where(value => value.FacePath.StartsWith(hair.SexFamily + ".", StringComparison.OrdinalIgnoreCase))
            .OrderBy(value => value.FacePath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault()
            ?? throw new InvalidDataException($"The {game} Global Morphs corpus has no deterministic {hair.SexFamily} face seed.");
        var seedPreview = await c.Load(seedFace.PackagePath, seedFace.FacePath);
        var seededDraft = seedPreview.Loaded.Document with { HairMeshReference = meshIdentity };
        var seeded = await c.Save(seededDraft, seedFace.PackagePath, $"{sourceKey}-seeded-face.pcc");
        c.Current.Evidence["nativeHairSeed"] = new
        {
            Source = sourceKey,
            FixtureFace = seedFace.FacePath,
            Mesh = meshIdentity,
            ExpectedMasterMaterial = hair.MasterMaterialName,
            ExpectedPreviewModel = hair.PreviewModel,
            NativeMaterial = sourceHairMaterial.Source,
            sourceHairMaterial.MasterMaterialName,
            sourceHairMaterial.PreviewModel,
            sourceHairMaterial.Scalars,
            sourceHairMaterial.Vectors,
            TextureIdentities = sourceHairMaterial.Textures.Values.Select(value => value.Texture.Source).ToArray()
        };

        var preview = await c.Load(seeded.PackagePath, seeded.FaceInstancedPath);
        QaContext.Require(preview.Loaded.Document.HairMeshReference is { } selected &&
                selected.InstancedPath.Equals(meshIdentity.InstancedPath, StringComparison.OrdinalIgnoreCase),
            $"Ordinary writer did not retain exact native hair selection '{meshIdentity.InstancedPath}'.");
        QaContext.Require(preview.Loaded.HairMesh is { } loadedHair &&
                loadedHair.Source.InstancedPath.Equals(meshIdentity.InstancedPath, StringComparison.OrdinalIgnoreCase),
            $"Face reload did not resolve the selected native hair mesh '{meshIdentity.InstancedPath}'.");

        using var scope = await c.Editor(preview, seeded.PackagePath);
        var editor = scope.Editor;
        editor.HairMesh.IsRandomisationLocked = true;
        editor.RandomiseMorphs = false;
        editor.RandomiseMaterials = true;
        QaContext.Require(editor.HairMesh.Value is { } lockedMesh &&
                lockedMesh.InstancedPath.Equals(meshIdentity.InstancedPath, StringComparison.OrdinalIgnoreCase),
            "Hair lock setup changed the selected native mesh.");

        var hairMaterial = RequireHairMaterialOnSelectedMesh(preview, meshIdentity,
            sourceHairMaterial.Source, "initial face load");
        AssertNativeHairGraph(hairMaterial, hair, "initial face load");

        var scalarControls = FindNativeHairControls<MaterialScalarEditorViewModel>(
            editor.Material, hairMaterial, MaterialParameterKind.Scalar);
        var vectorControls = FindNativeHairControls<MaterialVectorEditorViewModel>(
            editor.Material, hairMaterial, MaterialParameterKind.Vector);
        QaContext.Require(scalarControls.Count + vectorControls.Count > 0,
            $"Native hair graph '{hair.MasterMaterialName}' exposed no editable scalar or vector controls.");
        AssertNativeHairControlCoverage(editor.Material, hairMaterial, scalarControls, vectorControls, hair);

        var originalState = editor.CreateDraft();
        var originalMaterialData = editor.Material.CaptureInterchangeData();
        var authoredEdit = CreateNativeHairEdit(scalarControls, vectorControls);
        QaContext.Require(authoredEdit.Scalars.Count + authoredEdit.Vectors.Count > 0,
            "Native hair edit fixture did not cover any controls.");
        var editWarnings = await editor.MergeMaterialDataAsync(authoredEdit);
        QaContext.Require(editWarnings.Count == 0,
            "Native hair BMO merge emitted unexpected warnings: " + string.Join(" | ", editWarnings));
        var editedState = editor.CreateDraft();
        QaContext.Require(!MorphFaceEditorStateComparer.Equals(originalState, editedState),
            "Native hair parameter edit did not change the face draft.");
        AssertNativeHairOverrides(authoredEdit, editor.Material.CaptureInterchangeData(), sourceKey, "BMO merge");
        AssertNativeHairAttachmentTexturesExcluded(authoredEdit, originalMaterialData,
            editor.Material.CaptureInterchangeData(), hairMaterial);

        editor.UndoCommand.Execute(null);
        AssertState(originalState, editor.CreateDraft(), "native hair BMO edit undo");
        editor.RedoCommand.Execute(null);
        AssertState(editedState, editor.CreateDraft(), "native hair BMO edit redo");

        var ronData = editor.Material.CaptureInterchangeData();
        var ronText = MaterialRonCodec.WriteMfe(FaceMaterialFileService.Capture(game, "QaNativeHair", ronData));
        var ronPath = c.Artifact($"{sourceKey}-material.ron");
        await c.Measure("Native hair material RON export", () =>
            MeshMaterialFileService.WriteAsync(ronPath, ronText, seeded.PackagePath), QaStageKind.FileTransfer);
        editor.UndoCommand.Execute(null);
        AssertState(originalState, editor.CreateDraft(), "native hair RON import setup undo");
        var (ronRead, ronWarnings) = await c.Measure("Native hair material RON import", async () =>
        {
            var read = FaceMaterialFileService.Read(await File.ReadAllTextAsync(ronPath), allowTse: false);
            var warnings = await editor.MergeMaterialDataAsync(read.Parameters);
            return (read, warnings);
        }, QaStageKind.FileTransfer);
        c.AddWarnings("Native hair material RON", ronPath, ronRead.Warnings.Concat(ronWarnings), seedFace.FacePath);
        QaContext.Require(ronWarnings.Count == 0,
            "Native hair RON import emitted unexpected warnings: " + string.Join(" | ", ronWarnings));
        var ronState = editor.CreateDraft();
        AssertNativeHairOverrides(authoredEdit, editor.Material.CaptureInterchangeData(), sourceKey, "RON roundtrip");
        AssertGeometry(originalState, ronState);
        editor.UndoCommand.Execute(null);
        AssertState(originalState, editor.CreateDraft(), "native hair RON import undo");
        editor.RedoCommand.Execute(null);
        AssertState(ronState, editor.CreateDraft(), "native hair RON import redo");

        if (sourceKey == "human-james-hair")
            await ExerciseJamesHairColourEdges(c, preview, editor, hairMaterial, vectorControls.Keys.ToArray(), authoredEdit);

        editor.HairMesh.IsRandomisationLocked = true;
        editor.MorphRandomisationStrength = 100;
        editor.MaterialRandomisationStrength = 0;
        var colourControlNames = vectorControls.Keys
            .Where(name => IsHairColourParameter(editor.Material.SourceParameterName(name)))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        QaContext.Require(colourControlNames.Length > 0,
            $"Native hair graph '{hair.MasterMaterialName}' has no visible colour controls to stress-test.");
        var colourBaseline = colourControlNames.ToDictionary(name => name,
            name => editor.Material.Vectors.Single(control => control.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value,
            StringComparer.OrdinalIgnoreCase);
        var changedAtFullStrength = colourControlNames.ToDictionary(name => name, _ => false,
            StringComparer.OrdinalIgnoreCase);
        var fullStrengthValues = colourControlNames.ToDictionary(name => name, _ => new HashSet<Vector4>(),
            StringComparer.OrdinalIgnoreCase);

        var rolls = await c.Randomise(editor, 4, $"Native hair strength/variation · {sourceKey}", afterRoll: roll =>
        {
            var values = CaptureHairControlValues(editor.Material, scalarControls.Keys, vectorControls.Keys);
            foreach (var value in values.Scalars)
                QaContext.Require(float.IsFinite(value.Value), $"Roll {roll} produced a non-finite native hair scalar '{value.Key}'.");
            foreach (var value in values.Vectors)
                QaContext.Require(IsFinite(value.Value), $"Roll {roll} produced a non-finite native hair vector '{value.Key}'.");

            if (roll == 1)
            {
                editor.MaterialRandomisationStrength = 50;
            }
            else if (roll == 2)
            {
                editor.MaterialRandomisationStrength = 100;
            }
            else
            {
                foreach (var controlName in colourControlNames)
                {
                    var value = editor.Material.Vectors.Single(control => control.Name.Equals(
                        controlName, StringComparison.OrdinalIgnoreCase)).Value;
                    fullStrengthValues[controlName].Add(value);
                    if (value != colourBaseline[controlName]) changedAtFullStrength[controlName] = true;
                }
            }
        });
        QaContext.Require(rolls.Successful == 4,
            $"Native hair randomisation completed {rolls.Successful}/4 rolls.");
        foreach (var controlName in colourControlNames)
        {
            QaContext.Require(changedAtFullStrength[controlName],
                $"Native hair colour control '{editor.Material.SourceParameterName(controlName)}' never changed at 100% strength.");
            QaContext.Require(fullStrengthValues[controlName].Count > 1,
                $"Native hair colour control '{editor.Material.SourceParameterName(controlName)}' produced no variation at 100% strength.");
        }
        QaContext.Require(editor.HairMesh.Value is { } finalHair &&
                finalHair.InstancedPath.Equals(meshIdentity.InstancedPath, StringComparison.OrdinalIgnoreCase),
            "Locked native hair mesh changed during the strength/variation checks.");

        var finalDraft = editor.CreateDraft();
        AssertGeometry(ronState, finalDraft);
        c.Current.Evidence["nativeHairRandomisation"] = new
        {
            Source = sourceKey,
            Rolls = rolls,
            StrengthPhases = new[] { new { FirstRoll = 1, LastRoll = 1, Strength = 0 },
                new { FirstRoll = 2, LastRoll = 2, Strength = 50 },
                new { FirstRoll = 3, LastRoll = 4, Strength = 100 } },
            ColourVariation = colourControlNames.Select(controlName => new
            {
                Parameter = editor.Material.SourceParameterName(controlName),
                ChangedAtFullStrength = changedAtFullStrength[controlName],
                DistinctFullStrengthValues = fullStrengthValues[controlName].Count
            }).ToArray(),
            HairMeshLocked = editor.HairMesh.IsRandomisationLocked,
            SelectedHairMesh = editor.HairMesh.Value
        };

        var saved = await c.Save(finalDraft, seeded.PackagePath, $"{sourceKey}-native-hair-final.pcc");
        var reopened = await c.Load(saved.PackagePath, saved.FaceInstancedPath);
        AssertGeometry(finalDraft, reopened.Loaded.Document);
        AssertMaterials(finalDraft, reopened.Loaded.Document);
        QaContext.Require(reopened.Loaded.Document.HairMeshReference is { } savedHair &&
                savedHair.InstancedPath.Equals(meshIdentity.InstancedPath, StringComparison.OrdinalIgnoreCase),
            $"Final save/reopen changed selected native hair mesh '{meshIdentity.InstancedPath}'.");
        var savedHairMaterial = RequireHairMaterialOnSelectedMesh(reopened, meshIdentity,
            sourceHairMaterial.Source, "final PCC reopen");
        AssertNativeHairGraph(savedHairMaterial, hair, "final PCC reopen");
        AssertNativeHairTextureIdentities(sourceHairMaterial, savedHairMaterial, sourceKey);
        AssertNativeHairOverrides(finalDraft.MaterialOverrides is { } savedOverrides
                ? new MorphFaceMaterialData(savedOverrides.Scalars, savedOverrides.Vectors, savedOverrides.Textures)
                : new MorphFaceMaterialData([], [], []),
            new MorphFaceMaterialData(reopened.Loaded.Document.MaterialOverrides.Scalars,
                reopened.Loaded.Document.MaterialOverrides.Vectors, reopened.Loaded.Document.MaterialOverrides.Textures),
            sourceKey, "final save/reopen");
        await c.Render(reopened.Scene, $"{sourceKey}-native-hair-reopened.png");
    }

    private static ResolvedHeadMaterial RequireHairMaterialOnSelectedMesh(
        MorphFacePreviewLoadResult preview,
        AssetIdentity expectedMesh,
        AssetIdentity expectedMaterial,
        string stage)
    {
        var hairMesh = preview.Loaded.HairMesh;
        QaContext.Require(hairMesh is not null && SameCanonicalAssetIdentity(hairMesh.Source, expectedMesh),
            $"The {stage} selected a different native hair mesh than '{expectedMesh.InstancedPath}'. " +
            $"Resolved '{hairMesh?.Source.InstancedPath ?? "<missing>"}' ({hairMesh?.Source.ClassName ?? "<missing>"}) " +
            $"from '{hairMesh?.Source.PackagePath ?? "<missing>"}'.");
        var matchingSlots = hairMesh!.RenderData?.MaterialSlots
            .Where(slot => slot is not null && SameCanonicalAssetIdentity(slot, expectedMaterial))
            .Cast<AssetIdentity>()
            .DistinctBy(MaterialIdentityKey.Create)
            .ToArray() ?? [];
        QaContext.Require(matchingSlots.Length == 1,
            $"Expected exactly one canonical native hair MIC '{expectedMaterial.InstancedPath}' " +
            $"on selected mesh '{expectedMesh.InstancedPath}' at {stage}; found {matchingSlots.Length}.");

        // The mesh has been materialized into the face PCC, so resolve the
        // preview graph through the actual slot identity (including its new
        // physical package path), not the installed donor's old package key.
        var slotIdentity = matchingSlots[0];
        var materialKey = MaterialIdentityKey.Create(slotIdentity);
        var matches = preview.Loaded.Materials.Materials.Values
            .Where(material => material.Family == HeadMaterialFamily.Hair &&
                MaterialIdentityKey.Create(material.Source).Equals(materialKey, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        QaContext.Require(matches.Length == 1,
            $"The {stage} did not resolve exactly one material graph from the selected hair mesh's " +
            $"native MIC '{slotIdentity.InstancedPath}' in '{slotIdentity.PackagePath}'.");
        return matches[0];
    }

    private static bool SameCanonicalAssetIdentity(AssetIdentity actual, AssetIdentity expected) =>
        actual.ClassName.Equals(expected.ClassName, StringComparison.OrdinalIgnoreCase) &&
        QaTextureIdentity.CanonicalPath(actual).Equals(
            QaTextureIdentity.CanonicalPath(expected), StringComparison.OrdinalIgnoreCase);

    private static NativeHairSource ResolveNativeHairSource(MorphFaceGame game, string sourceKey) =>
        (game, sourceKey) switch
        {
            (MorphFaceGame.LE1 or MorphFaceGame.LE2, "human-hair") => new(sourceKey, "HMM",
                "BIOG_HMM_HIR_PRO_R.Short02.HMM_HIR_PROShort02_MDL",
                "HMN_HED_PRO_MASTER_HAIR_MAT", HumanMaterialPreviewModel.Default),
            (MorphFaceGame.LE3, "human-hair") => new(sourceKey, "HMM",
                "BIOG_HMM_HIR_PRO_R.Hair_Short02.HMM_HIR_PROShort02_MDL_CC",
                "HMN_HED_PRO_MASTER_HAIR_MAT_CC", HumanMaterialPreviewModel.CcHair),
            (MorphFaceGame.LE3, "human-advanced-hair") => new(sourceKey, "HMM",
                "BIOG_HMM_HIR_PRO_R.Hair_Short02.HMM_HIR_PROShort02_MDL",
                "HMN_HED_PRO_MASTER_HAIR_MAT", HumanMaterialPreviewModel.Default),
            (MorphFaceGame.LE1 or MorphFaceGame.LE2, "human-iconic-femshep-hair") => new(sourceKey, "HMF",
                "BIOG_HMF_HIR_PRO.Hair_PROShepard.HMF_HIR_PROShepard_MDL",
                "HMN_HED_PRO_ME3_MASTER_Hair_MAT", HumanMaterialPreviewModel.Default),
            (MorphFaceGame.LE3, "human-james-hair") => new(sourceKey, "HMM",
                "BIOG_HMM_HIR_PRO_R.Hair_James.HMM_HIR_PROJames_MDL",
                "Human_Hair_MASTER", HumanMaterialPreviewModel.HumanHair),
            _ => throw new ArgumentOutOfRangeException(nameof(sourceKey),
                $"Native hair source '{sourceKey}' is not defined for {game}.")
        };

    private static void AssertNativeHairGraph(ResolvedHeadMaterial material, NativeHairSource expected, string stage)
    {
        QaContext.Require(material.Family == HeadMaterialFamily.Hair &&
                material.MasterMaterialName.Equals(expected.MasterMaterialName, StringComparison.OrdinalIgnoreCase) &&
                material.PreviewModel == expected.PreviewModel,
            $"Native hair graph changed at {stage}: expected {expected.MasterMaterialName} / {expected.PreviewModel}; " +
            $"got {material.MasterMaterialName} / {material.PreviewModel}.");
    }

    private static Dictionary<string, T> FindNativeHairControls<T>(
        MaterialEditorViewModel material,
        ResolvedHeadMaterial hair,
        MaterialParameterKind kind)
    {
        var sourceNames = kind switch
        {
            MaterialParameterKind.Scalar => hair.Scalars.Keys.Concat(hair.SupportedScalars),
            MaterialParameterKind.Vector => hair.Vectors.Keys.Concat(hair.SupportedVectors),
            MaterialParameterKind.Texture => hair.Textures.Keys.Concat(hair.SupportedTextures),
            _ => []
        };
        var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var sourceName in sourceNames.Distinct(StringComparer.OrdinalIgnoreCase)
                     .Where(name => hair.Supports(name, kind)))
        {
            var controlName = FindNativeHairControlName(material, sourceName, kind);
            if (controlName is null) continue;
            object? control = kind switch
            {
                MaterialParameterKind.Scalar => material.Scalars.FirstOrDefault(value => value.Name == controlName),
                MaterialParameterKind.Vector => material.Vectors.FirstOrDefault(value => value.Name == controlName),
                MaterialParameterKind.Texture => material.Textures.FirstOrDefault(value => value.Name == controlName),
                _ => null
            };
            if (control is T typed) result.Add(controlName, typed);
        }
        return result;
    }

    private static string? FindNativeHairControlName(
        MaterialEditorViewModel material,
        string sourceName,
        MaterialParameterKind kind)
    {
        IEnumerable<string> names = kind switch
        {
            MaterialParameterKind.Scalar => material.Scalars.Select(value => value.Name),
            MaterialParameterKind.Vector => material.Vectors.Select(value => value.Name),
            MaterialParameterKind.Texture => material.Textures.Select(value => value.Name),
            _ => []
        };
        var matches = names.Where(name => material.SourceParameterName(name).Equals(
            sourceName, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.FirstOrDefault(name => material.ParameterScopeKey(name) is "human") ??
               matches.FirstOrDefault(name => material.ParameterScopeKey(name) is null) ??
               matches.FirstOrDefault();
    }

    private static void AssertNativeHairControlCoverage(
        MaterialEditorViewModel material,
        ResolvedHeadMaterial hair,
        IReadOnlyDictionary<string, MaterialScalarEditorViewModel> scalars,
        IReadOnlyDictionary<string, MaterialVectorEditorViewModel> vectors,
        NativeHairSource expected)
    {
        var inertParameters = new[]
        {
            "Aniso_Highlight_1", "Aniso_Highlight_2"
        };
        foreach (var name in inertParameters.Where(name => !hair.Supports(name, MaterialParameterKind.Scalar)))
            QaContext.Require(FindNativeHairControlName(material, name, MaterialParameterKind.Scalar) is null,
                $"Native hair UI exposed inert scalar '{name}' for graph '{hair.MasterMaterialName}'.");

        if (expected.SourceKey == "human-james-hair")
        {
            foreach (var name in new[] { "Aniso_Highlight_1", "Aniso_Highlight_2" })
                QaContext.Require(scalars.Values.Any(value => material.SourceParameterName(value.Name).Equals(
                        name, StringComparison.OrdinalIgnoreCase)),
                    $"James Human_Hair_MASTER UI omitted scalar '{name}'.");
            foreach (var name in new[] { "Hair_Color", "Aniso_Color_1", "Aniso_Color_2" })
                QaContext.Require(vectors.Values.Any(value => material.SourceParameterName(value.Name).Equals(
                        name, StringComparison.OrdinalIgnoreCase)),
                    $"James Human_Hair_MASTER UI omitted vector '{name}'.");
            QaContext.Require(scalars.Count(value => material.SourceParameterName(value.Key)
                    is "Aniso_Highlight_1" or "Aniso_Highlight_2") == 2 &&
                vectors.Count(value => material.SourceParameterName(value.Key)
                    is "Hair_Color" or "Aniso_Color_1" or "Aniso_Color_2") == 3,
                "James Hair exposes other than the five native Human_Hair_MASTER metadata controls.");
        }
    }

    private static MorphFaceMaterialData CreateNativeHairEdit(
        IReadOnlyDictionary<string, MaterialScalarEditorViewModel> scalars,
        IReadOnlyDictionary<string, MaterialVectorEditorViewModel> vectors)
    {
        var scalarEdits = scalars.Values.Select(control =>
        {
            var direction = control.Value <= (control.Minimum + control.Maximum) / 2f ? 1f : -1f;
            var value = Math.Clamp(control.Value + direction * 0.137f, control.Minimum, control.Maximum);
            if (value == control.Value)
                value = Math.Clamp(control.Value - direction * 0.137f, control.Minimum, control.Maximum);
            return new ScalarMaterialOverride(control.Name, value);
        }).ToArray();
        var vectorEdits = vectors.Values.Select(control => new VectorMaterialOverride(control.Name,
            new Vector4(AdjustHairColour(control.Value.X), AdjustHairColour(control.Value.Y),
                AdjustHairColour(control.Value.Z), control.Value.W))).ToArray();
        return new MorphFaceMaterialData(scalarEdits, vectorEdits, []);
    }

    private static float AdjustHairColour(float value) => value > 0.5f ? value - 0.173f : value + 0.173f;

    private static async Task ExerciseJamesHairColourEdges(
        QaContext c,
        MorphFacePreviewLoadResult preview,
        FaceEditorViewModel editor,
        ResolvedHeadMaterial hair,
        IReadOnlyCollection<string> vectorControlNames,
        MorphFaceMaterialData restore)
    {
        var colourControls = new[] { "Hair_Color", "Aniso_Color_1", "Aniso_Color_2" }
            .Select(parameter => vectorControlNames.SingleOrDefault(name =>
                editor.Material.SourceParameterName(name).Equals(parameter, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"James Human_Hair_MASTER vector '{parameter}' is not available for edge testing."))
            .ToArray();

        var white = new MorphFaceMaterialData([], colourControls.Select(name => new VectorMaterialOverride(
            name, new Vector4(1, 1, 1, CurrentHairVector(editor.Material, name).W))).ToArray(), []);
        await MergeAndAssertHairColours(editor, white, colourControls, "all-white Human_Hair_MASTER preview");
        c.Current.Evidence["humanHairMasterWhiteColours"] = colourControls.Select(name => new
            { Parameter = editor.Material.SourceParameterName(name), Value = CurrentHairVector(editor.Material, name) }).ToArray();
        await c.Render(c.Scene(preview, editor), "james-hair-all-white.png");

        var zero = new MorphFaceMaterialData([], colourControls.Select(name => new VectorMaterialOverride(
            name, new Vector4(0, 0, 0, CurrentHairVector(editor.Material, name).W))).ToArray(), []);
        await MergeAndAssertHairColours(editor, zero, colourControls, "zero-RGB Human_Hair_MASTER preview");
        c.Current.Evidence["humanHairMasterZeroColours"] = colourControls.Select(name => new
            { Parameter = editor.Material.SourceParameterName(name), Value = CurrentHairVector(editor.Material, name) }).ToArray();
        await c.Render(c.Scene(preview, editor), "james-hair-zero-rgb.png");

        var restoreVectors = restore.Vectors.Where(value => colourControls.Any(name =>
            name.Equals(value.Name, StringComparison.OrdinalIgnoreCase))).ToArray();
        QaContext.Require(restoreVectors.Length == colourControls.Length,
            "James Hair edge setup could not restore all three Human_Hair_MASTER colour values.");
        var restoreData = new MorphFaceMaterialData([], restoreVectors, []);
        await editor.MergeMaterialDataAsync(restoreData);
        QaContext.Require(colourControls.All(name => IsFinite(CurrentHairVector(editor.Material, name))),
            $"Restoring native hair colours produced a non-finite value for '{hair.MasterMaterialName}'.");
    }

    private static async Task MergeAndAssertHairColours(
        FaceEditorViewModel editor,
        MorphFaceMaterialData data,
        IReadOnlyList<string> controls,
        string stage)
    {
        var warnings = await editor.MergeMaterialDataAsync(data);
        QaContext.Require(warnings.Count == 0, $"{stage} emitted warnings: " + string.Join(" | ", warnings));
        foreach (var value in data.Vectors)
        {
            var controlName = controls.Single(item => item.Equals(value.Name, StringComparison.OrdinalIgnoreCase));
            var currentValue = CurrentHairVector(editor.Material, controlName);
            QaContext.Require(currentValue == value.Value && IsFinite(currentValue),
                $"{stage} did not retain finite RGB values for '{editor.Material.SourceParameterName(controlName)}'.");
        }
    }

    private static Vector4 CurrentHairVector(MaterialEditorViewModel material, string controlName) =>
        material.Vectors.Single(control => control.Name.Equals(controlName, StringComparison.OrdinalIgnoreCase)).Value;

    private static void AssertNativeHairOverrides(
        MorphFaceMaterialData expected,
        MorphFaceMaterialData actual,
        string sourceKey,
        string stage)
    {
        foreach (var value in expected.Scalars)
        {
            var matches = actual.Scalars.Where(candidate => candidate.Name.Equals(value.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            QaContext.Require(matches.Length == 1 && Math.Abs(matches[0].Value - value.Value) <= 0.00001f,
                $"Native hair scalar '{value.Name}' differs after {stage} for '{sourceKey}'.");
        }
        foreach (var value in expected.Vectors)
        {
            var matches = actual.Vectors.Where(candidate => candidate.Name.Equals(value.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            QaContext.Require(matches.Length == 1 && Vector4.Distance(matches[0].Value, value.Value) <= 0.00001f,
                $"Native hair vector '{value.Name}' differs after {stage} for '{sourceKey}'.");
        }
    }

    private static void AssertNativeHairAttachmentTexturesExcluded(
        MorphFaceMaterialData changed,
        MorphFaceMaterialData before,
        MorphFaceMaterialData captured,
        ResolvedHeadMaterial hair)
    {
        QaContext.Require(changed.Textures.Count == 0,
            "Native attachment textures were incorrectly sent through the face BMO merge.");
        QaContext.Require(MaterialSignature(new MorphFaceMaterialData([], [], before.Textures)) ==
                MaterialSignature(new MorphFaceMaterialData([], [], captured.Textures)),
            $"Native hair texture controls changed face BMO texture state for graph '{hair.MasterMaterialName}'.");
    }

    private static (Dictionary<string, float> Scalars, Dictionary<string, Vector4> Vectors) CaptureHairControlValues(
        MaterialEditorViewModel material,
        IEnumerable<string> scalarNames,
        IEnumerable<string> vectorNames) =>
        (scalarNames.ToDictionary(name => name,
             name => material.Scalars.Single(control => control.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value,
             StringComparer.OrdinalIgnoreCase),
         vectorNames.ToDictionary(name => name,
             name => material.Vectors.Single(control => control.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value,
             StringComparer.OrdinalIgnoreCase));

    private static void AssertNativeHairTextureIdentities(
        ResolvedHeadMaterial expected,
        ResolvedHeadMaterial actual,
        string sourceKey)
    {
        foreach (var (parameter, expectedBinding) in expected.Textures)
        {
            QaContext.Require(actual.Textures.TryGetValue(parameter, out var actualBinding) &&
                    QaTextureIdentity.SameTexture(expectedBinding.Texture.Source, actualBinding.Texture.Source),
                $"Native hair texture identity '{parameter}' changed after final save/reopen for '{sourceKey}': " +
                $"expected '{QaTextureIdentity.CanonicalPath(expectedBinding.Texture.Source)}'; got " +
                $"'{(actualBinding is null ? "<missing>" : QaTextureIdentity.CanonicalPath(actualBinding.Texture.Source))}'.");
        }
        QaContext.Require(expected.Textures.Count == actual.Textures.Count,
            $"Native hair texture parameter count changed after final save/reopen for '{sourceKey}'.");
    }

    private static bool IsHairColourParameter(string parameterName) => parameterName is
        "Hair_Color" or "HED_Hair_Colour_Vector" or "Highlight1Color" or "Highlight2Color" or
        "Highlight1Colour_Vector" or "Highlight2Colour_Vector" or "Aniso_Color_1" or "Aniso_Color_2";

    private static bool IsFinite(Vector4 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);
}
