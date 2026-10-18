using System.Numerics;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;
using MorphFaceEditor.ViewModels;
using LegendaryExplorerCore.Packages;

namespace MorphFaceEditor.QA;

internal static partial class QaCases
{
    private static readonly string[][] HairColourAliases =
    [
        ["Hair_Color", "HED_Hair_Colour_Vector"],
        ["Aniso_Color_1", "HED_Aniso_Colour01_Vector", "Highlight1Color", "Highlight1Colour_Vector"],
        ["Aniso_Color_2", "HED_Aniso_Colour02_Vector", "Highlight2Color", "Highlight2Colour_Vector"]
    ];

    private static async Task HumanCustomMaterials(QaContext c, string sex)
    {
        RequireHumanCase(c, sex);
        var source = c.Source();
        var preview = await c.Load(source.PackagePath, source.FacePath);
        var mesh = ImportedMeshFromHead(preview.Loaded.BaseHead);
        c.TrackInput(mesh.SourcePath);
        var reader = c.CustomReader;
        var detached = await LoadCustomWorkspace(c, mesh, c.Face.Game);
        var ids = new[]
        {
            $"{c.Face.Game}:{sex}-npc-face:", $"{c.Face.Game}:{sex}:",
            $"{c.Face.Game}:{sex}:", $"{c.Face.Game}:{sex}-player:"
        };
        var families = new[]
        {
            HeadMaterialFamily.Skin, HeadMaterialFamily.Skin,
            HeadMaterialFamily.Scalp, HeadMaterialFamily.Scalp
        };
        var slots = detached.CustomMaterials?.UsedSlots ??
            throw new InvalidDataException("Native human mesh did not create a Custom material workspace.");
        QaContext.Require(slots.Count == 4,
            $"The representative {sex} head has {slots.Count} material slots; the reviewed face/scalp matrix needs four.");

        var assigned = new List<CustomMaterialAssignmentOption>();
        for (var index = 0; index < ids.Length; index++)
        {
            var option = RequireOption(detached, ids[index], families[index]);
            assigned.Add(option);
            detached.CustomMaterials!.Assign(slots[index], option);
        }

        AssertHumanChoiceOrder(detached.MaterialOptions, sex);
        c.Current.Evidence["humanMaterialAssignments"] = assigned.Select(option => new
        {
            option.Id,
            option.Family,
            Source = option.Template.Source,
            option.Template.MasterMaterialName
        }).ToArray();
        AssertNativeTemplatePaths(c.Face.Game, sex, assigned);

        using var scope = await c.Editor(detached.Preview, mesh.SourcePath, detached: detached);
        await EditRonHistoryAndSave(c, reader, detached, scope.Editor, assigned);
    }

    private static async Task HumanCustomHair(QaContext c, string sourceKey)
    {
        var game = c.Face.Game;
        var sex = sourceKey.Equals("human-iconic-femshep-hair", StringComparison.OrdinalIgnoreCase)
            ? "human-female" : "human-male";
        RequireHumanCase(c, sex);

        // The native HMM/HMF source head supplies a real rigged four-slot mesh
        // while the selected graph remains the exact catalogue donor.
        var source = c.Source();
        var nativePreview = await c.Load(source.PackagePath, source.FacePath);
        var mesh = ImportedMeshFromHead(nativePreview.Loaded.BaseHead);
        c.TrackInput(mesh.SourcePath);
        var reader = c.CustomReader;
        var detached = await LoadCustomWorkspace(c, mesh, game);
        var workspace = detached.CustomMaterials ?? throw new InvalidDataException("Custom hair workspace was not created.");

        var hairKey = (sourceKey, game) switch
        {
            ("human-hair", MorphFaceGame.LE1 or MorphFaceGame.LE2) => "human-hair",
            ("human-hair", MorphFaceGame.LE3) => "human-hair",
            ("human-iconic-femshep-hair", MorphFaceGame.LE1 or MorphFaceGame.LE2) => "human-iconic-femshep-hair",
            ("human-advanced-hair", MorphFaceGame.LE1 or MorphFaceGame.LE2) => "human-iconic-femshep-hair",
            ("human-advanced-hair", MorphFaceGame.LE3) => "human-advanced-hair",
            ("human-james-hair", MorphFaceGame.LE3) => "human-james-hair",
            _ => throw new InvalidDataException($"Unsupported Custom hair QA route '{sourceKey}' for {game}.")
        };
        var prefix = $"{game}:{hairKey}:";
        var hair = detached.MaterialOptions.FirstOrDefault(option =>
            option.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            option.Family is HeadMaterialFamily.Hair or HeadMaterialFamily.MaskedHair)
            ?? throw new InvalidDataException($"The expected native material option '{prefix}' is missing. " +
                string.Join("; ", detached.Preview.Loaded.Warnings));

        var slots = workspace.UsedSlots;
        QaContext.Require(slots.Count >= (hairKey == "human-james-hair" ? 2 : 1),
            "The representative human head lacks slots for its Custom hair material assignments.");
        workspace.Assign(slots[0], hair);
        var assignments = new List<CustomMaterialAssignmentOption> { hair };
        if (hairKey == "human-james-hair")
        {
            // Exercise distinct Hair and Scalp graphs in the shared Human scope.
            // Custom Mode permits only one material identity for the Hair role.
            var scalp = RequireOption(detached, $"{game}:human-male-player:", HeadMaterialFamily.Scalp);
            workspace.Assign(slots[1], scalp);
            assignments.Add(scalp);
            foreach (var slot in slots.Skip(2)) workspace.Assign(slot, hair);
        }
        else
        {
            foreach (var slot in slots.Skip(1)) workspace.Assign(slot, hair);
        }

        c.Current.Evidence["humanHairAssignments"] = assignments.Select(option => new
        {
            option.Id,
            option.Family,
            Source = option.Template.Source,
            option.Template.MasterMaterialName
        }).ToArray();
        AssertNativeTemplatePaths(game, sex, assignments);

        using var scope = await c.Editor(detached.Preview, mesh.SourcePath, detached: detached);
        var editor = scope.Editor;
        var hairDiffuseName = editor.Material.Textures.FirstOrDefault(control =>
            control.Name.Equals("HAIR_Diff", StringComparison.OrdinalIgnoreCase) ||
            editor.Material.SourceParameterName(control.Name).Equals("HAIR_Diff", StringComparison.OrdinalIgnoreCase));
        if (hair.Template.Supports("HAIR_Diff", MaterialParameterKind.Texture))
        {
            QaContext.Require(hairDiffuseName is not null,
                $"Custom {hairKey} supports HAIR_Diff but does not expose its authored texture control.");
        }
        else if (hairKey == "human-james-hair")
        {
            QaContext.Require(hair.Template.Supports("Hair_Stack", MaterialParameterKind.Texture) &&
                editor.Material.Textures.Any(control => editor.Material.SourceParameterName(control.Name)
                    .Equals("Hair_Stack", StringComparison.OrdinalIgnoreCase)),
                "The James graph should expose its authored Hair_Stack texture control instead of HAIR_Diff.");
        }
        else
        {
            throw new InvalidDataException($"Custom {hairKey} exposes neither the expected HAIR_Diff texture nor a reviewed James exception.");
        }

        if (hairDiffuseName is not null)
        {
            // Custom geometry need not have a hair attachment. Prove that the
            // exposed diffuse can actually be edited, exported and undone.
            var faceTemplate = RequireOption(detached, $"{game}:{sex}:", HeadMaterialFamily.Skin);
            var replacement = faceTemplate.Template.Textures.GetValueOrDefault("HED_Diff")?.Texture.Source
                ?? throw new InvalidDataException("The native face template has no colour texture for the Custom diffuse edit.");
            var textureBefore = MaterialSignature(editor.Material.CaptureInterchangeData());
            var warnings = await editor.MergeMaterialDataAsync(new MorphFaceMaterialData([], [],
                [new TextureMaterialOverride(hairDiffuseName.Name, replacement)]));
            QaContext.Require(warnings.Count == 0, "Custom hair diffuse edit failed: " + string.Join("; ", warnings));
            var textureAfter = MaterialSignature(editor.Material.CaptureInterchangeData());
            QaContext.Require(textureBefore != textureAfter,
                "Editing the Custom hair diffuse did not change its captured material texture.");
            editor.UndoCommand.Execute(null);
            QaContext.Require(MaterialSignature(editor.Material.CaptureInterchangeData()) == textureBefore,
                "Undo did not restore the Custom hair diffuse texture.");
            editor.RedoCommand.Execute(null);
            QaContext.Require(MaterialSignature(editor.Material.CaptureInterchangeData()) == textureAfter,
                "Redo did not restore the Custom hair diffuse edit.");
            c.Current.Evidence["customHairDiffuseEdit"] = replacement;
        }

        var baseline = editor.Material.CaptureInterchangeData();
        if (hair.Template.Supports("HAIR_Diff", MaterialParameterKind.Texture))
            QaContext.Require(baseline.Textures.Any(value =>
                    editor.Material.SourceParameterName(value.Name).Equals("HAIR_Diff", StringComparison.OrdinalIgnoreCase)),
                "The Custom hair diffuse parameter is visible but missing from material interchange data.");
        if (hairKey == "human-james-hair")
        {
            c.Current.Evidence["jamesScalpColourSupport"] = new
            {
                Assignments = assignments.Select(option => new
                {
                    option.Id,
                    option.Template.MasterMaterialName,
                    SupportedVectors = option.Template.SupportedVectors
                        .Order(StringComparer.OrdinalIgnoreCase).ToArray()
                }).ToArray(),
                ExposedVectors = editor.Material.Vectors
                    .Where(value => string.Equals(editor.Material.ParameterScopeKey(value.Name),
                        "human", StringComparison.OrdinalIgnoreCase))
                    .Select(value => new
                    {
                        Control = value.Name,
                        SourceParameterName = editor.Material.SourceParameterName(value.Name),
                        Scope = editor.Material.ParameterScopeKey(value.Name)
                    })
                    .OrderBy(value => value.SourceParameterName, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            };
        }
        var colourNames = TrackRandomisedHairColours(editor, baseline,
            assignments.Select(option => option.Template).ToArray());
        if (hairKey == "human-james-hair")
            RequireJamesScalpColours(editor, hair, assignments[1], colourNames);
        var perRollChanges = colourNames.Keys.ToDictionary(key => key, _ => 0, StringComparer.OrdinalIgnoreCase);
        var previous = CaptureColourValues(editor, colourNames);
        var summary = await c.Randomise(editor, 3, $"Custom {hairKey} colour randomisation", afterRoll: _ =>
        {
            var current = CaptureColourValues(editor, colourNames);
            foreach (var group in colourNames.Keys)
            {
                if (current[group].Any(value => !float.IsFinite(value.Value.X) || !float.IsFinite(value.Value.Y) ||
                                                 !float.IsFinite(value.Value.Z) || !float.IsFinite(value.Value.W)))
                    throw new InvalidDataException($"Randomisation produced a non-finite {group} colour.");
                if (current[group].Any(value => !previous[group].TryGetValue(value.Key, out var before) || before != value.Value))
                    perRollChanges[group]++;
            }
            previous = current;
        });
        QaContext.Require(summary.Successful == 3, $"Custom hair colour randomisation completed only {summary.Successful}/3 rolls.");
        var randomised = editor.Material.CaptureInterchangeData();
        foreach (var (group, changes) in perRollChanges)
            QaContext.Require(changes > 0, $"The 3-roll batch never changed an actual {group} control in this Custom hair scope.");
        c.Current.Evidence["hairColourRandomisation"] = new
        {
            NativeControls = colourNames,
            RollsWithChanges = perRollChanges,
            Initial = baseline.Vectors,
            Final = randomised.Vectors
        };

        await EditRonHistoryAndSave(c, reader, detached, editor, assignments);
    }

    private static async Task EditRonHistoryAndSave(QaContext c, MorphFacePackageReader reader,
        DetachedMeshPreviewLoadResult detached, FaceEditorViewModel editor,
        IReadOnlyList<CustomMaterialAssignmentOption> assignments)
    {
        var workspace = detached.CustomMaterials ?? throw new InvalidDataException("Custom material workspace was not created.");
        var game = c.Face.Game;
        var before = editor.Material.CaptureInterchangeData();
        var requested = PerturbEditableValues(before);
        QaContext.Require(requested.Scalars.Count + requested.Vectors.Count > 0,
            "Selected human materials expose no editable numeric controls for the RON/PCC roundtrip.");
        editor.ApplyMeshMaterialImport(new PreparedMeshMaterialImport(null, requested,
            new Dictionary<string, DecodedTextureAsset?>(), []));
        var edited = editor.Material.CaptureInterchangeData();
        QaContext.Require(MaterialSignature(requested) == MaterialSignature(edited),
            "Custom material editing did not retain all requested native scalar/vector values.");

        var ronPath = c.Artifact("custom-human-materials.ron");
        var ron = MaterialRonCodec.WriteMfe(MeshMaterialFileService.Capture(workspace, game,
            Path.GetFileNameWithoutExtension(workspace.Source.SourcePath), edited));
        await MeshMaterialFileService.WriteAsync(ronPath, ron, workspace.Source.SourcePath);
        var references = new PackageReferenceService(reader, c.Textures);
        var prepared = await new MeshMaterialFileService(references).PrepareAsync(
            ron, false, workspace, editor.CustomMaterialOptions, game);

        editor.UndoCommand.Execute(null);
        QaContext.Require(MaterialSignature(before) == MaterialSignature(editor.Material.CaptureInterchangeData()),
            "One undo did not restore the Custom materials before the RON edit.");
        editor.ApplyMeshMaterialImport(prepared);
        QaContext.Require(MaterialSignature(edited) == MaterialSignature(editor.Material.CaptureInterchangeData()),
            "Custom MFE RON did not restore the edited values and texture identities.");
        var afterRon = editor.Material.CaptureInterchangeData();
        editor.UndoCommand.Execute(null);
        QaContext.Require(MaterialSignature(before) == MaterialSignature(editor.Material.CaptureInterchangeData()),
            "Undo did not remove the Custom MFE RON import.");
        editor.RedoCommand.Execute(null);
        QaContext.Require(MaterialSignature(afterRon) == MaterialSignature(editor.Material.CaptureInterchangeData()),
            "Redo did not restore the Custom MFE RON import.");

        var registry = await c.Textures.ReadAsync(game);
        if (!registry.IsAvailable) throw new QaPrerequisiteException($"{game} asset database must be Ready to save Custom human materials.");
        var saved = await c.Measure("Custom human PCC material export and verification", () => Task.Run(() =>
            new CustomMeshPccMaterializer().Save(new CustomMeshPccSaveRequest(workspace.Source, workspace,
                editor.Material.CaptureInterchangeData(), game, c.Artifact("custom-human-materials.pcc"),
                "QaHumanMaterials", new ImportedSkeletalMeshPackageWriter(), registry.Candidates))), QaStageKind.FileTransfer);
        var warningReviews = QaWarningDiagnostics.InspectSavedPackage(saved.PackagePath, saved.Warnings);
        if (warningReviews.Length > 0) c.Current.Evidence["customHumanSaveWarnings"] = warningReviews;
        c.AddWarningReviews("Custom human material export", saved.PackagePath, warningReviews,
            relatedPath: workspace.Source.SourcePath);

        using var reopenedReader = new MorphFacePackageReader();
        var reopened = c.MeasureSync("Custom human PCC fresh material readback", () =>
            reopenedReader.LoadDetachedAttachment(saved.PackagePath, saved.MeshPath), QaStageKind.Load);
        VerifyCustomMaterialReadback(workspace, assignments, editor.Material.CaptureInterchangeData(), reopened);
        var loaded = detached.Preview.Loaded with
        {
            BaseHead = reopened.Mesh,
            Materials = reopened.Materials,
            UsesCustomBaseMesh = true,
            Document = detached.Preview.Loaded.Document with { BaseHeadReference = reopened.Source }
        };
        c.Current.Evidence["savedCustomHumanMesh"] = saved;
        c.Current.Evidence["savedCustomHumanMaterialGraph"] = assignments.Select(option => new
        {
            option.Id,
            ExpectedMaster = option.Template.MasterMaterialName,
            ExpectedSource = option.Template.Source
        }).ToArray();
        await c.Render(c.Scenes.Create(loaded), "custom-human-materials-reopened.png");
    }

    private static void VerifyCustomMaterialReadback(CustomMaterialWorkspace workspace,
        IReadOnlyList<CustomMaterialAssignmentOption> assignments,
        MorphFaceMaterialData expectedData, LoadedAttachment reopened)
    {
        var scoped = MeshMaterialInterchange.Split(expectedData).GetValueOrDefault("human")
            ?? new MorphFaceMaterialData([], [], []);
        var renderData = reopened.Mesh.RenderData ?? throw new InvalidDataException("Saved Custom human mesh lost its render data.");
        QaContext.Require(reopened.Mesh.Positions.Length == workspace.Source.Positions.Length &&
            reopened.Mesh.Positions.Zip(workspace.Source.Positions).All(pair => Vector3.Distance(pair.First, pair.Second) <= 0.00001f),
            "Custom human save/reopen changed its authored mesh positions.");
        QaContext.Require(renderData.Indices.SequenceEqual(workspace.Source.Indices),
            "Custom human save/reopen changed its authored index stream.");
        foreach (var assignment in workspace.Assignments)
        {
            var slotIndex = assignment.Slot.MaterialIndex;
            QaContext.Require(slotIndex >= 0 && slotIndex < renderData.MaterialSlots.Count,
                $"Saved Custom slot {slotIndex} is outside the reopened mesh's material slots.");
            var identity = renderData.MaterialSlots[slotIndex];
            var actual = identity is null ? null : reopened.Materials.Find(identity);
            QaContext.Require(actual is not null, $"Saved Custom slot {slotIndex} lost its material graph.");
            var expected = assignment.Option;
            QaContext.Require(actual!.Family == expected.Family &&
                actual.MasterMaterialName.Equals(expected.Template.MasterMaterialName, StringComparison.OrdinalIgnoreCase),
                $"Saved Custom slot {slotIndex} changed its selected family or native master material.");
            QaContext.Require(actual.Source.PackagePath.Equals(reopened.Source.PackagePath, StringComparison.OrdinalIgnoreCase),
                $"Saved Custom slot {slotIndex} did not materialise its exact MIC into the output PCC.");
            foreach (var scalar in scoped.Scalars.Where(value => expected.Template.Supports(value.Name, MaterialParameterKind.Scalar) &&
                         CustomMeshPccMaterializer.IsSerializableMaterialParameter(value.Name)))
                QaContext.Require(actual.Scalars.TryGetValue(scalar.Name, out var scalarValue) &&
                    Math.Abs(scalarValue - scalar.Value) <= 0.00001f,
                    $"Saved Custom slot {slotIndex} scalar '{scalar.Name}' differs after reopen.");
            foreach (var vector in scoped.Vectors.Where(value => expected.Template.Supports(value.Name, MaterialParameterKind.Vector) &&
                         CustomMeshPccMaterializer.IsSerializableMaterialParameter(value.Name)))
                QaContext.Require(actual.Vectors.TryGetValue(vector.Name, out var vectorValue) &&
                    Vector4.Distance(vectorValue, vector.Value) <= 0.00001f,
                    $"Saved Custom slot {slotIndex} vector '{vector.Name}' differs after reopen.");
            foreach (var texture in scoped.Textures.Where(value => value.TextureReference is not null &&
                         expected.Template.Supports(value.Name, MaterialParameterKind.Texture) &&
                         CustomMeshPccMaterializer.IsSerializableMaterialParameter(value.Name)))
            {
                var restored = actual.Textures.GetValueOrDefault(texture.Name)?.Texture.Source;
                QaContext.Require(restored is not null && QaTextureIdentity.SameTexture(texture.TextureReference!, restored),
                    $"Saved Custom slot {slotIndex} texture '{texture.Name}' changed identity after reopen.");
            }
        }
    }

    private static async Task<DetachedMeshPreviewLoadResult> LoadCustomWorkspace(QaContext c,
        ImportedMeshAsset mesh, MorphFaceGame game)
    {
        var detached = await c.LoadCustomWorkspace(mesh, game);
        QaContext.Require(detached.CustomMaterials is not null, "Human native mesh did not create Custom material slots.");
        QaContext.Require(!detached.Preview.EditingSession.CanEditMorphFeatures,
            "A native mesh reused as Custom geometry exposed unsupported morph editing.");
        c.AddWarnings("Human Custom template catalogue", mesh.SourcePath, detached.Detached.Warnings);
        return detached;
    }

    private static ImportedMeshAsset ImportedMeshFromHead(SkeletalMeshAsset head)
    {
        var lod = head.Lods.FirstOrDefault(value => value.LodIndex == 0)
            ?? throw new InvalidDataException($"Native head '{head.Source.InstancedPath}' has no renderable LOD0.");
        var authoredSections = lod.Topology.Sections.Where(value => value.TriangleCount > 0)
            .OrderBy(value => value.BaseIndex).ToArray();
        var usedSlotCount = authoredSections
            .Select(value => value.MaterialIndex).Distinct().Count();
        QaContext.Require(usedSlotCount <= CustomMaterialWorkspace.MaximumSupportedSlots,
            $"Native head '{head.Source.InstancedPath}' has {usedSlotCount} used material slots, above the supported Custom limit.");
        var slotMap = authoredSections.Select(value => value.MaterialIndex).Distinct().Order()
            .Select((sourceIndex, targetIndex) => (sourceIndex, targetIndex))
            .ToDictionary(value => value.sourceIndex, value => value.targetIndex);
        var indices = lod.RenderData!.Indices.ToList();
        var sections = authoredSections.Select(value => new ImportedMeshSection(slotMap[value.MaterialIndex],
                $"Material {slotMap[value.MaterialIndex]}", value.BaseIndex, checked(value.TriangleCount * 3))).ToList();
        var occupiedSlots = sections.Select(value => value.MaterialIndex).ToHashSet();
        if (occupiedSlots.Count < 4)
        {
            var seed = sections[0];
            foreach (var index in Enumerable.Range(0, 4).Where(index => !occupiedSlots.Contains(index)))
            {
                var duplicateIndexStart = indices.Count;
                indices.AddRange(lod.RenderData!.Indices.Skip(seed.IndexStart).Take(seed.IndexCount));
                sections.Add(seed with
                {
                    MaterialIndex = index,
                    MaterialName = $"QA material {index}",
                    IndexStart = duplicateIndexStart
                });
                occupiedSlots.Add(index);
            }
        }
        QaContext.Require(sections.Count > 0 && sections.All(value => value.IndexStart >= 0 && value.IndexCount > 0 &&
            value.IndexStart + value.IndexCount <= indices.Count),
            $"Native head '{head.Source.InstancedPath}' has invalid material sections for Custom testing.");
        var bones = lod.Topology.ReferenceSkeleton.Select(value =>
            new ImportedMeshBone(value.Name, value.ParentIndex, value.Position, value.Orientation)).ToArray();
        return new ImportedMeshAsset(head.Source.PackagePath,
            lod.Positions.ToArray(), lod.Normals.ToArray(), lod.RenderData!.Tangents.ToArray(),
            lod.RenderData!.TextureCoordinates.ToArray(), indices.ToArray(), sections.ToArray(), bones,
            lod.RenderData!.BoneIndices.ToArray(), lod.RenderData!.BoneWeights.ToArray(),
            Enumerable.Range(0, lod.Positions.Length).ToArray());
    }

    private static CustomMaterialAssignmentOption RequireOption(DetachedMeshPreviewLoadResult detached,
        string idPrefix, HeadMaterialFamily family) => detached.MaterialOptions.FirstOrDefault(option =>
        option.Id.StartsWith(idPrefix, StringComparison.OrdinalIgnoreCase) && option.Family == family)
        ?? throw new InvalidDataException($"The exact Custom material option '{idPrefix}' ({family}) is missing. " +
            string.Join("; ", detached.Preview.Loaded.Warnings));

    private static void RequireHumanCase(QaContext c, string sex)
    {
        QaContext.Require(sex is "human-male" or "human-female", $"Unknown human material case key '{sex}'.");
        QaContext.Require(c.Face.ProfileSuffix.Equals(sex, StringComparison.OrdinalIgnoreCase),
            $"Human QA case '{sex}' received a {c.Face.ProfileSuffix} source face.");
    }

    private static void AssertHumanChoiceOrder(IReadOnlyList<CustomMaterialAssignmentOption> options, string sex)
    {
        var choices = options.Select((option, index) => (option, index))
            .Where(value => value.option.AppearanceCompatibilityKey.Equals(sex, StringComparison.OrdinalIgnoreCase) &&
                value.option.Family is HeadMaterialFamily.Skin or HeadMaterialFamily.Scalp or HeadMaterialFamily.Eyes or HeadMaterialFamily.Lashes)
            .ToArray();
        QaContext.Require(choices.Any(value => value.option.Family == HeadMaterialFamily.Eyes) &&
            choices.Any(value => value.option.Family == HeadMaterialFamily.Lashes),
            $"The {sex} material catalogue lost its Eye or Lash choices.");
        var eye = choices.Where(value => value.option.Family == HeadMaterialFamily.Eyes).Min(value => value.index);
        var lash = choices.Where(value => value.option.Family == HeadMaterialFamily.Lashes).Min(value => value.index);
        var faceAndScalp = choices.Where(value => value.option.Family is HeadMaterialFamily.Skin or HeadMaterialFamily.Scalp)
            .Select(value => value.index).ToArray();
        QaContext.Require(faceAndScalp.Length >= 4 && faceAndScalp.Max() < eye && eye < lash,
            $"The {sex} choices are not ordered Face, Scalp, Eye, Lash.");
    }

    private static void AssertNativeTemplatePaths(MorphFaceGame game, string sex,
        IReadOnlyList<CustomMaterialAssignmentOption> assignments)
    {
        foreach (var option in assignments)
        {
            var parts = option.Id.Split(':', 3);
            QaContext.Require(parts.Length == 3 && parts[0].Equals(game.ToString(), StringComparison.OrdinalIgnoreCase),
                $"Custom material route '{option.Id}' does not contain a stable game/key/source identity.");
            QaContext.Require(parts[2].Equals(option.Template.Source.InstancedPath, StringComparison.OrdinalIgnoreCase),
                $"Custom material route '{option.Id}' changed its exact native donor identity.");
            var expectedMaster = ExpectedHumanMaster(game, sex, parts[1], option.Family);
            QaContext.Require(option.Template.MasterMaterialName.Equals(expectedMaster, StringComparison.OrdinalIgnoreCase),
                $"Custom material route '{option.Id}' resolves to '{option.Template.MasterMaterialName}', expected '{expectedMaster}'.");
            QaContext.Require(option.Id.StartsWith($"{game}:{sex}", StringComparison.OrdinalIgnoreCase) ||
                option.Family is HeadMaterialFamily.Hair or HeadMaterialFamily.MaskedHair,
                $"Custom material route '{option.Id}' does not preserve the requested human identity.");
        }
    }

    private static string ExpectedHumanMaster(MorphFaceGame game, string sex, string sourceKey, HeadMaterialFamily family)
    {
        if (family == HeadMaterialFamily.Skin)
        {
            if (sourceKey.EndsWith("-npc-face", StringComparison.OrdinalIgnoreCase))
                return sex == "human-male" ? "HMM_HED_PRONPC_MASTER_FACE_MAT" : "HMF_HED_PRO_MASTER_FACE_MAT";
            return sex == "human-male" ? "HMM_HED_PROCustom_MASTER_FACE_MAT" : "HMF_HED_PROCustom_MASTER_FACE_MAT";
        }
        if (family == HeadMaterialFamily.Scalp)
            return game == MorphFaceGame.LE3 && sourceKey.EndsWith("-player", StringComparison.OrdinalIgnoreCase)
                ? Le3CcScalpMaterialPolicy.MasterMaterialPath.Split('.').Last()
                : "HMM_HED_PRO_MASTER_SCALP_MAT";
        if (family is HeadMaterialFamily.Hair or HeadMaterialFamily.MaskedHair)
        {
            return sourceKey switch
            {
                "human-hair" => game == MorphFaceGame.LE3 ? "HMN_HED_PRO_MASTER_HAIR_MAT_CC" : "HMN_HED_PRO_MASTER_HAIR_MAT",
                "human-iconic-femshep-hair" => "HMN_HED_PRO_ME3_MASTER_Hair_MAT",
                "human-advanced-hair" => "HMN_HED_PRO_MASTER_HAIR_MAT",
                "human-james-hair" => "Human_Hair_MASTER",
                _ => throw new InvalidDataException($"No reviewed native master expectation exists for '{sourceKey}'.")
            };
        }
        throw new InvalidDataException($"Human template '{sourceKey}' has unexpected material family {family}.");
    }

    private static MorphFaceMaterialData PerturbEditableValues(MorphFaceMaterialData source) => new(
        source.Scalars.Select(value => new ScalarMaterialOverride(value.Name, value.Value + 0.0007f)).ToArray(),
        source.Vectors.Select(value => new VectorMaterialOverride(value.Name, value.Value + new Vector4(0.0007f))).ToArray(),
        source.Textures.ToArray());

    private static Dictionary<string, string[]> TrackRandomisedHairColours(FaceEditorViewModel editor,
        MorphFaceMaterialData baseline, IReadOnlyList<ResolvedHeadMaterial> templates)
    {
        var available = editor.Material.Vectors.Where(control =>
                string.Equals(editor.Material.ParameterScopeKey(control.Name), "human", StringComparison.OrdinalIgnoreCase))
            .GroupBy(control => editor.Material.SourceParameterName(control.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(control => control.Name).ToArray(), StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var supportedGroups = new HashSet<int>();
        foreach (var template in templates)
        {
            for (var index = 0; index < HairColourAliases.Length; index++)
            {
                var aliases = HairColourAliases[index];
                var supported = template.SupportedVectors
                    .Where(name => aliases.Contains(name, StringComparer.OrdinalIgnoreCase)).ToArray();
                if (supported.Length == 0) continue;
                supportedGroups.Add(index);
                foreach (var parameter in supported)
                {
                    var controlNames = available.GetValueOrDefault(parameter) ?? [];
                    QaContext.Require(controlNames.Length > 0,
                        $"Custom graph '{template.Source.InstancedPath}' supports '{parameter}' but does not expose its control.");
                    result[$"{template.Source.InstancedPath}:{parameter}"] = controlNames;
                }
            }
        }
        QaContext.Require(supportedGroups.Contains(0), "The selected Custom human hair graphs expose no Hair colour field.");
        QaContext.Require(baseline.Vectors.Count > 0, "Custom human hair material has no vector values to randomise.");
        return result;
    }

    private static void RequireJamesScalpColours(FaceEditorViewModel editor,
        CustomMaterialAssignmentOption hair, CustomMaterialAssignmentOption scalp,
        IReadOnlyDictionary<string, string[]> tracked)
    {
        var hairColours = new[] { "Hair_Color", "Aniso_Color_1", "Aniso_Color_2" };
        const string scalpColour = "HED_Hair_Colour_Vector";
        QaContext.Require(hairColours.All(name => hair.Template.Supports(name, MaterialParameterKind.Vector)) &&
                          scalp.Template.Supports(scalpColour, MaterialParameterKind.Vector),
            "The James/CC Scalp fixture lacks its expected native hair colour parameters.");
        foreach (var parameter in hairColours.Append(scalpColour))
        {
            var control = editor.Material.Vectors.FirstOrDefault(value =>
                editor.Material.SourceParameterName(value.Name).Equals(parameter, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(editor.Material.ParameterScopeKey(value.Name), "human", StringComparison.OrdinalIgnoreCase));
            QaContext.Require(control is not null, $"The James/CC Scalp scope hid native vector '{parameter}'.");
            QaContext.Require(tracked.Values.SelectMany(value => value).Contains(control!.Name, StringComparer.OrdinalIgnoreCase),
                $"The mixed native alias control '{parameter}' is not participating in Custom human hair randomisation.");
        }
    }

    private static Dictionary<string, Dictionary<string, Vector4>> CaptureColourValues(FaceEditorViewModel editor,
        IReadOnlyDictionary<string, string[]> controls) => controls.ToDictionary(group => group.Key,
        group => group.Value.ToDictionary(name => name,
            name => editor.Material.Vectors.First(value => value.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value,
            StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
}
