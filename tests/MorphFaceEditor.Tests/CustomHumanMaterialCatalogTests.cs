using System.Numerics;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Core.Services;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.LegendaryExplorer.TextureRegistry;
using MorphFaceEditor.Services;
using MorphFaceEditor.ViewModels;

namespace MorphFaceEditor.Tests;

internal static class CustomHumanMaterialCatalogTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("custom human NPC Player and hair choices retain native evidence across games", ChoicesAndControls),
        new("custom LE3 James hair and Player scalp edits survive PCC reopen", NewGraphsRoundTrip)
    ];

    private static void ChoicesAndControls()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        using var reader = new MorphFacePackageReader();
        var catalog = new CustomMaterialTemplateCatalogService(reader);
        foreach (var game in new[] { MorphFaceGame.LE1, MorphFaceGame.LE2, MorphFaceGame.LE3 })
        {
            var result = catalog.Load(game);
            var options = result.Options;
            foreach (var sex in new[] { "Male", "Female" })
            foreach (var role in new[] { "Face", "Scalp" })
            foreach (var variant in new[] { "NPC", "Player" })
            {
                var label = $"Human {sex} - {role} ({variant})";
                TestAssert.True(options.Count(value => value.Label == label) == 1,
                    $"{game} expected one '{label}'. Options: {string.Join(" | ", options.Select(value => value.Label))}. " +
                    $"Warnings: {string.Join(" | ", result.Warnings.Take(12))}");
                var option = options.Single(value => value.Label == label);
                if (role == "Face" && variant == "Player" || role == "Scalp" && variant == "NPC")
                    TestAssert.True(option.Id.StartsWith($"{game}:human-{sex.ToLowerInvariant()}:", StringComparison.Ordinal),
                        $"Renaming {label} changed its pre-existing assignment ID.");
                TestAssert.Equal(role == "Face" ? HeadMaterialFamily.Skin : HeadMaterialFamily.Scalp, option.Family);
                TestAssert.Equal($"human-{sex.ToLowerInvariant()}", option.AppearanceCompatibilityKey);
                TestAssert.True(!option.Template.IsPreviewOnlyAttachment && option.Template.ContributesFaceControls,
                    $"{game} {label} is not an authoring template.");
                if (role == "Face")
                    TestAssert.Equal(variant == "Player"
                        ? $"{(sex == "Male" ? "HMM" : "HMF")}_HED_PROCustom_MASTER_FACE_MAT"
                        : sex == "Male" ? "HMM_HED_PRONPC_MASTER_FACE_MAT" : "HMF_HED_PRO_MASTER_FACE_MAT",
                        option.Template.MasterMaterialName);
                if (role == "Scalp")
                    TestAssert.Equal(game == MorphFaceGame.LE3 && variant == "Player"
                        ? HumanMaterialPreviewModel.CcScalp : HumanMaterialPreviewModel.Default, option.Template.PreviewModel);

                var workspace = new CustomMaterialWorkspace(CreateMesh());
                workspace.Assign(0, option);
                var session = new MaterialEditingSession(MorphFaceMaterialOverrides.Empty, workspace.ActiveMaterials);
                TestAssert.True(session.ScalarNames.Count + session.VectorNames.Count + session.TextureParameters.Count > 0,
                    $"{game} {label} exposes no controls after assignment.");
                // Export metadata retains the stable material identity independently of its label.
                var document = MeshMaterialFileService.Capture(workspace, game, "HumanMaterial",
                    new MorphFaceMaterialData([], [], []));
                var reopened = MaterialRonCodec.ReadMfe(MaterialRonCodec.WriteMfe(document));
                TestAssert.Equal(option.Id, reopened.Slots.Single().MaterialId);
            }

            var hair = options.Single(value => value.Label == "Human Hair");
            TestAssert.True(options.Take(12).Select(value => value.Label).SequenceEqual(
                new[] { "Human Male - Face (NPC)", "Human Male - Face (Player)",
                    "Human Male - Scalp (NPC)", "Human Male - Scalp (Player)",
                    "Human Male - Eyes", "Human Male - Lashes",
                    "Human Female - Face (NPC)", "Human Female - Face (Player)",
                    "Human Female - Scalp (NPC)", "Human Female - Scalp (Player)",
                    "Human Female - Asari Eyes", "Human Female - Asari Lashes" }),
                $"{game} did not group the human face/scalp choices together.");
            TestAssert.Equal(game == MorphFaceGame.LE3 ? "HMN_HED_PRO_MASTER_HAIR_MAT_CC"
                : "HMN_HED_PRO_MASTER_HAIR_MAT", hair.Template.MasterMaterialName);
            var advanced = options.Single(value => value.Label == "Human Advanced Hair");
            TestAssert.True(advanced.Template.MasterMaterialName.Equals(game == MorphFaceGame.LE3
                ? "HMN_HED_PRO_MASTER_HAIR_MAT" : "HMN_HED_PRO_ME3_MASTER_Hair_MAT", StringComparison.OrdinalIgnoreCase),
                $"{game} Advanced Hair did not use the intended native graph.");
            TestAssert.True(!options.Any(value => value.Template.PreviewModel == HumanMaterialPreviewModel.Kaiden),
                $"{game} exposed Kaidan's preview-only graph as an authoring choice.");
            var james = options.SingleOrDefault(value => value.Label == "James Vega Hair");
            TestAssert.True((game == MorphFaceGame.LE3) == (james is not null), "James hair appeared in the wrong game.");

            foreach (var option in new[] { hair, advanced, james }.OfType<CustomMaterialAssignmentOption>())
            {
                var workspace = new CustomMaterialWorkspace(CreateMesh());
                workspace.Assign(0, option);
                var session = new MaterialEditingSession(MorphFaceMaterialOverrides.Empty, workspace.ActiveMaterials);
                TestAssert.True(session.TextureParameters.Count > 0, $"{game} {option.Label} lost its texture controls.");
                if (option.Template.Textures.TryGetValue("HAIR_Diff", out var diffuse))
                {
                    var references = new PackageReferenceService(reader,
                        new TextureCatalogService(new TextureRegistryStore(TextureRegistryPaths.CreateDefault())));
                    using var editor = new MaterialEditorViewModel(session, new WpfHdrColorDialogService(), references,
                        string.Empty, [], message => throw new Exception(message), new DetachedMeshFeatureMetadataCatalog(workspace));
                    var name = MaterialParameterControlKey.Create("human", "HAIR_Diff");
                    TestAssert.True(editor.Textures.Any(value => value.Name == name),
                        $"{game} {option.Label} hides hair diffuse on a Custom material slot.");
                    var replacement = diffuse.Texture with
                    {
                        Source = new AssetIdentity("custom-diffuse.pcc", "Custom.HairDiffuse", 1, "Texture2D")
                    };
                    session.SetTextureReference(name, replacement);
                    TestAssert.Equal(replacement.Source, session.CaptureInterchangeData().Textures.Single(value => value.Name == name).TextureReference);
                    TestAssert.Equal(replacement.Source, session.CreateOverrides().Textures.Single(value => value.Name == name).TextureReference);
                    using var faceEditor = new MaterialEditorViewModel(
                        new MaterialEditingSession(MorphFaceMaterialOverrides.Empty,
                            new ResolvedHeadMaterialSet(new Dictionary<string, ResolvedHeadMaterial> { [option.Template.Key] = option.Template })),
                        new WpfHdrColorDialogService(), references, string.Empty, [],
                        message => throw new Exception(message), new HumanMaleFeatureMetadataCatalog());
                    TestAssert.True(faceEditor.Textures.All(value => value.Name != "HAIR_Diff"),
                        "Custom hair diffuse visibility leaked into a normal face workspace.");
                }
                if (option == hair && game != MorphFaceGame.LE3)
                    TestAssert.True(!session.ScalarNames.Any(name => name.Contains("Highlight", StringComparison.OrdinalIgnoreCase)),
                        $"{game} exposed compiled-out ordinary hair highlight controls in Custom Mode.");
                if (option == james)
                {
                    TestAssert.Equal(HumanMaterialPreviewModel.HumanHair, option.Template.PreviewModel);
                    foreach (var name in new[] { "Hair_Color", "Aniso_Color_1", "Aniso_Color_2" })
                        TestAssert.True(session.VectorNames.Contains(MaterialParameterControlKey.Create("human", name)),
                            $"James hair lost {name} in Custom Mode.");
                    foreach (var name in new[] { "Aniso_Highlight_1", "Aniso_Highlight_2" })
                        TestAssert.True(session.ScalarNames.Contains(MaterialParameterControlKey.Create("human", name)),
                            $"James hair lost {name} in Custom Mode.");
                    TestAssert.True(session.TextureParameters.Any(value =>
                        value.Name == MaterialParameterControlKey.Create("human", "Hair_Stack")),
                        "Custom authoring did not expose James's MIC-owned Hair_Stack texture.");
                }
            }
        }
    }

    private static void NewGraphsRoundTrip()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        using var reader = new MorphFacePackageReader();
        var options = new CustomMaterialTemplateCatalogService(reader).Load(MorphFaceGame.LE3).Options;
        var source = CreateMesh(twoSlots: true);
        var workspace = new CustomMaterialWorkspace(source);
        workspace.Assign(0, options.Single(value => value.Label == "James Vega Hair"));
        workspace.Assign(1, options.Single(value => value.Label == "Human Female - Scalp (Player)"));
        var data = new MorphFaceMaterialData(
            [new ScalarMaterialOverride("@human/Aniso_Highlight_1", 7),
             new ScalarMaterialOverride("@human/HED_Scalp_Buzzcut_Alpha_Scalar", 0.35f)],
            [new VectorMaterialOverride("@human/Hair_Color", new Vector4(0.8f, 0.6f, 0.4f, 1))], []);
        var output = Path.Combine(Path.GetTempPath(), $"MFE-Custom-Human-Hair-{Guid.NewGuid():N}.pcc");
        try
        {
            var saved = new CustomMeshPccMaterializer().Save(new CustomMeshPccSaveRequest(
                source, workspace, data, MorphFaceGame.LE3, output, "CustomHumanHair",
                new ImportedSkeletalMeshPackageWriter(), []));
            using var package = MEPackageHandler.OpenMEPackage(output, forceLoadFromDisk: true);
            PackageIntegrity.Verify(package);
            var evidence = reader.LoadMeshMaterialEvidence(output, saved.MeshPath);
            var hair = evidence.Slots.Single(value => value.SlotIndex == 0).Material;
            var scalp = evidence.Slots.Single(value => value.SlotIndex == 1).Material;
            TestAssert.Equal(HumanMaterialPreviewModel.HumanHair, hair.PreviewModel);
            TestAssert.Equal(HumanMaterialPreviewModel.CcScalp, scalp.PreviewModel);
            TestAssert.Equal(7f, hair.Scalars["Aniso_Highlight_1"]);
            TestAssert.Equal(new Vector4(0.8f, 0.6f, 0.4f, 1), hair.Vectors["Hair_Color"]);
            TestAssert.Equal(0.35f, scalp.Scalars["HED_Scalp_Buzzcut_Alpha_Scalar"]);
            TestAssert.True(hair.Textures.ContainsKey("Hair_Stack"), "Saved James graph lost Hair_Stack.");
            foreach (var material in saved.Materials)
            {
                var mic = package.FindExport(material.MaterialPath, "MaterialInstanceConstant")!;
                TestAssert.True(mic.Parent is not null && mic.GetProperty<ObjectProperty>("Parent") is { Value: not 0 },
                    "A new custom material lost its ancestry or shader parent.");
            }
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    private static ImportedMeshAsset CreateMesh(bool twoSlots = false) => new(
        "custom-human-materials.gltf",
        [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
        [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
        [new(Vector3.UnitX, 1), new(Vector3.UnitX, 1), new(Vector3.UnitX, 1)],
        [Vector2.Zero, Vector2.UnitX, Vector2.UnitY],
        twoSlots ? [0, 1, 2, 0, 1, 2] : [0, 1, 2],
        twoSlots ? [new(0, "Hair", 0, 3), new(1, "Scalp", 3, 3)] : [new(0, "Material", 0, 3)],
        [], null, null, [0, 1, 2]);
}
