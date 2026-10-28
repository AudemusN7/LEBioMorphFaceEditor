using MorphFaceEditor.Core.Deformation;
using System.Numerics;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Services;
using MorphFaceEditor.ViewModels;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;
using MorphFaceEditor.Infrastructure;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;

using static MorphFaceEditor.Tests.UiTestFixtures;

namespace MorphFaceEditor.Tests;

internal static class UiProfileTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("unsupported profiles keep unsafe geometry controls unavailable", UnsupportedProfilesKeepGeometryClosed),
        new("TUF face with mismatched bake remains a material preview", LegacyTufPlaceholderRemainsMaterialPreview),
        new("native Turian and Krogan loads preserve unknown authored rows without controls", UnknownAuthoredRowsRemainLoadable),
        new("ALN loads preserve authored bakes without a target set, including static faces", TargetlessAlnPreservesAuthoredGeometry),
        new("malformed LE3 HMF hair and inert HMM eye metadata remain hidden", MalformedAndInertTargetsStayHidden)
    ];

    private static void LegacyTufPlaceholderRemainsMaterialPreview() => WpfTestDispatcher.Invoke(() => RunWithDispatcher(async () =>
    {
        var path = TufInstalledDonorFixture.CreateMismatchedBakeCopy();
        try
        {
            var originalHash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path));
            var catalog = await new MorphFaceCatalogService().ReadAsync(path);
            var face = catalog.Faces.Single(value => value.ProfileKey == "le3-female-turian" && value.ObjectName == "TUF_Authored_Neutral");
            var original = new MorphFacePackageContextService().CaptureMorphData(path, face.InstancedPath);
            using var reader = new MorphFacePackageReader();
            using var shell = CreateMainWindowViewModel(reader);
            await shell.OpenDroppedFileAsync(path);
            shell.SelectedFace = shell.Faces.Single(value => value.InstancedPath == face.InstancedPath);
            await ((AsyncRelayCommand)shell.LoadSelectedFaceCommand).ExecuteAsync();
            var editor = shell.Editor ?? throw new Exception(shell.ErrorMessage ?? "Legacy TUF face failed to load.");
            TestAssert.True(shell.HasPreview && !shell.HasError, "Legacy TUF face did not publish a material preview.");
            TestAssert.True(!editor.CanEditMorphFeatures && editor.Bones.Count == 0 && editor.Features.Count == 0,
                "Legacy TUF placeholder geometry became editable.");
            TestAssert.True(editor.AllowsMaterialRandomisation, "Legacy TUF placeholder lost material editing.");
            var draft = editor.CreateDraft();
            PackageTestFixtures.AssertMorphEqual(original, new MorphFaceMorphData(draft.MorphFeatures, draft.FinalSkeleton, draft.BakedLods));
            TestAssert.True(originalHash.SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))),
                "Loading the legacy TUF face changed its source PCC.");
        }
        finally { File.Delete(path); }
    }));

    private static void UnknownAuthoredRowsRemainLoadable()
    {
        const string unknown = "UnrecognisedAuthoredFeature";
        var fixturePath = PackageTestFixtures.FixturePath("LE2 GlobalMorphs.pcc");
        var original = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(fixturePath));
        foreach (var archetype in new[] { "turian", "krogan" })
            PackageTestFixtures.WithPackageCopy("LE2 GlobalMorphs.pcc", path => RunWithDispatcher(async () =>
            {
                var catalog = await new MorphFaceCatalogService().ReadAsync(path);
                var selected = catalog.Faces.First(face => face.ProfileKey == "le2-" + archetype);
                using (var package = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true))
                {
                    var face = package.FindExport(selected.InstancedPath, "BioMorphFace")!;
                    var properties = face.GetProperties();
                    properties.GetProp<ArrayProperty<StructProperty>>("m_aMorphFeatures")!.Add(
                        new StructProperty("MorphFeature", false,
                            new NameProperty(unknown, "sFeatureName"), new FloatProperty(0, "Offset")));
                    face.WriteProperties(properties);
                    package.Save();
                }
                var authoredSource = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path));
                using var reader = new MorphFacePackageReader();
                using var shell = CreateMainWindowViewModel(reader);
                TestAssert.True(shell.CanOpenDroppedFile(path), "The authored PCC was not accepted by normal file drop.");
                await shell.OpenDroppedFileAsync(path);
                shell.SelectedFace = shell.Faces.Single(face => face.InstancedPath == selected.InstancedPath);
                TestAssert.True(shell.LoadSelectedFaceCommand.CanExecute(null), "The authored face could not be selected for loading.");
                await ((AsyncRelayCommand)shell.LoadSelectedFaceCommand).ExecuteAsync();
                var editor = shell.Editor ?? throw new Exception(shell.ErrorMessage ?? "The authored face failed to load.");
                TestAssert.True(shell.HasPreview && !shell.HasError, "The authored face did not publish a usable preview.");
                TestAssert.Equal(0f, editor.CreateDraft().MorphFeatures.Single(value => value.Name == unknown).Offset);
                TestAssert.True(editor.Features.All(value => value.Name != unknown), "An unresolved authored row became a morph control.");
                TestAssert.True(authoredSource.SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))),
                    "Loading the authored face changed its source PCC.");
            }));
        TestAssert.True(original.SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(fixturePath))),
            "The native fixture changed during authored-row loading.");
    }

    private static void UnsupportedProfilesKeepGeometryClosed()
    {
        using var reader = new MorphFacePackageReader();
        var profiles = MorphFaceProfileRegistry.CreateDefault().Profiles;
        foreach (var (key, bones) in new[]
                 { ("le2-vorcha", true), ("le2-female-batarian", false) })
        {
            var profile = profiles.Single(value => value.Key == key);
            using var editor = CreateEditor(reader, profile.GetUiProfile(), key, ignoresAuthoredGeometry: !bones);
            TestAssert.Equal(0, editor.Features.Count);
            TestAssert.True(bones ? editor.Bones.Count > 0 : editor.Bones.Count == 0,
                $"{key} exposed unsupported bones or hid its valid rig.");
            TestAssert.True(!editor.AllowsMorphRandomisation && !editor.RandomiseMorphs,
                $"{key} exposed unsafe morph randomisation.");
            editor.RandomiseMorphs = true;
            TestAssert.True(!editor.RandomiseMorphs, $"{key} accepted unsafe morph randomisation.");
            TestAssert.True(editor.AllowsMaterialRandomisation, $"{key} lost material editing.");
            if (!bones)
                TestAssert.True(!editor.CanEditMorphFeatures && !editor.CanEditAttachments,
                    $"{key} exposed authored geometry or attachment edits.");
        }
    }

    private static void TargetlessAlnPreservesAuthoredGeometry()
    {
        var hasNativeOffsetBake = false;
        foreach (var game in new[] { MorphFaceGame.LE2, MorphFaceGame.LE3 })
        PackageTestFixtures.WithPackageCopy($"{game} GlobalMorphs.pcc", path =>
        {
            var profiles = MorphFaceProfileRegistry.CreateDefault();
            var targets = new MorphTargetCatalog();
            var reader = new MorphFacePackageReader();
            using var loader = new MorphFacePreviewLoadService(new HeadPreviewSceneFactory(), targets, profiles, reader);
            var candidates = MorphFaceReferenceInspector.Inspect(path)
                .Where(face => profiles.Find(face.Game, face.FacePath, face.BaseHeadPath)?.Archetype == "vorcha")
                .Select(face => reader.Load(path, face.FacePath, decodeTextures: false))
                .ToArray();
            var authored = candidates.FirstOrDefault(face => face.Document.BakedLods.Count > 0 &&
                               !face.Document.BakedLods[0].SequenceEqual(face.BaseHead.Positions)) ?? candidates.First();
            var offsetCount = candidates.Count(face => face.Document.BakedLods.Count > 0 &&
                !face.Document.BakedLods[0].SequenceEqual(face.BaseHead.Positions));
            hasNativeOffsetBake |= offsetCount > 0;
            Console.WriteLine($"{game} ALN native fixtures: {candidates.Length} faces, " +
                $"{candidates.Count(face => face.Document.BakedLods.Count > 0)} baked, {offsetCount} offset-baked.");
            var loaded = loader.LoadAsync(path, authored.Document.Source.InstancedPath).GetAwaiter().GetResult();
            var session = loaded.EditingSession;
            TestAssert.True(loaded.Profile.TargetSetName is null && targets.Load(loaded.Profile).Count == 0,
                $"{game} ALN still depends on a reconstructed target bundle.");
            if (authored.Document.BakedLods.Count == 0)
            {
                TestAssert.True(!session.CanEditMorphFeatures && !session.CanFixMorph,
                    "A native static ALN face exposed target-based geometry edits.");
                AssertPreviewPositions(authored.BaseHead.Positions, loaded.Scene.Meshes[0].Vertices);
                AssertAuthoredAln(authored.Document, session.CreateDraft(authored.Document.HairMeshReference,
                    authored.Document.OtherMeshReferences, loaded.MaterialEditingSession.CreateOverrides()));
                AssertAlnConversionUnavailable(path, authored.Document.Source.InstancedPath);
                return;
            }
            TestAssert.Equal(MorphFaceGeometryMode.FixedBake, session.GeometryMode);
            TestAssert.True(!session.CanEditMorphFeatures && !session.CanFixMorph,
                $"{game} ALN exposed morph editing or destructive target-based repair.");
            TestAssert.True(session.CanEditBones, $"{game} ALN lost its valid bone rig.");
            AssertPreviewPositions(authored.Document.BakedLods[0], loaded.Scene.Meshes[0].Vertices);
            var explicitRelative = loader.LoadAsync(path, authored.Document.Source.InstancedPath,
                geometryMode: MorphFaceGeometryMode.RelativeBake).GetAwaiter().GetResult();
            TestAssert.Equal(MorphFaceGeometryMode.FixedBake, explicitRelative.EditingSession.GeometryMode);
            TestAssert.True(!explicitRelative.EditingSession.CanEditMorphFeatures &&
                !explicitRelative.EditingSession.CanFixMorph && explicitRelative.EditingSession.CanEditBones,
                "An explicit RON geometry mode bypassed ALN's fixed-bake capabilities.");
            AssertAuthoredAln(authored.Document, explicitRelative.EditingSession.CreateDraft(
                authored.Document.HairMeshReference, authored.Document.OtherMeshReferences,
                explicitRelative.MaterialEditingSession.CreateOverrides()));

            var draft = session.CreateDraft(authored.Document.HairMeshReference,
                authored.Document.OtherMeshReferences, loaded.MaterialEditingSession.CreateOverrides());
            AssertAuthoredAln(authored.Document, draft);
            var bone = session.FinalSkeleton.First();
            session.SetBoneAxis(bone.BoneName, 0, bone.Translation.X + 0.125f);
            draft = session.CreateDraft(draft.HairMeshReference, draft.OtherMeshReferences, draft.MaterialOverrides);
            var expected = authored.Document with { FinalSkeleton = session.FinalSkeleton.ToArray() };
            AssertAuthoredAln(expected, draft);
            new MorphFacePackageWriter().SaveExisting(draft);
            reader.InvalidatePackage(path);
            var saved = reader.Load(path, draft.Source.InstancedPath, decodeTextures: false).Document;
            AssertAuthoredAln(expected, saved);
            AssertAlnConversionUnavailable(path, saved.Source.InstancedPath);

            // Empty feature arrays must not make an absent target set appear safe
            // to edit or offer repair which would erase an authored topography.
            foreach (var baked in new[] { saved.BakedLods, authored.BaseHead.Lods.Select(lod => lod.Positions).ToArray() })
            {
                var noFeatures = saved with { MorphFeatures = [], BakedLods = baked };
                var targetless = new MorphFaceEditingSession(noFeatures, authored.BaseHead, [],
                    metadataOnlyFeatures: loaded.Profile.MetadataOnlyFeatures);
                TestAssert.True(!targetless.CanEditMorphFeatures && !targetless.CanFixMorph,
                    "An empty feature list enabled targetless editing or repair.");
                AssertAuthoredAln(noFeatures, targetless.CreateDraft(noFeatures.HairMeshReference,
                    noFeatures.OtherMeshReferences, noFeatures.MaterialOverrides));
                var relative = new MorphFaceEditingSession(noFeatures, authored.BaseHead, [],
                    metadataOnlyFeatures: loaded.Profile.MetadataOnlyFeatures,
                    geometryMode: MorphFaceGeometryMode.RelativeBake);
                TestAssert.True(!relative.CanEditMorphFeatures && !relative.CanFixMorph && relative.CanEditBones,
                    "A targetless relative-bake session exposed morph controls or lost its valid rig.");
                AssertAuthoredAln(noFeatures, relative.CreateDraft(noFeatures.HairMeshReference,
                    noFeatures.OtherMeshReferences, noFeatures.MaterialOverrides));
            }

            using (var package = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true))
            {
                package.FindExport(draft.Source.InstancedPath, "BioMorphFace")!
                    .WriteBinary(new BioMorphFace { LODs = [] });
                package.Save();
            }
            reader.InvalidatePackage(path);
            var staticFace = loader.LoadAsync(path, draft.Source.InstancedPath).GetAwaiter().GetResult();
            TestAssert.True(!staticFace.EditingSession.CanEditMorphFeatures && !staticFace.EditingSession.CanFixMorph,
                "A static ALN face exposed target-based geometry edits.");
            TestAssert.Equal(0, staticFace.Loaded.Document.BakedLods.Count);
            TestAssert.True(saved.FinalSkeleton.SequenceEqual(staticFace.Loaded.Document.FinalSkeleton),
                "Loading a static ALN face changed its stored skeleton.");
            AssertPreviewPositions(authored.BaseHead.Positions, staticFace.Scene.Meshes[0].Vertices);
        });
        TestAssert.True(hasNativeOffsetBake, "The ALN preservation regression did not exercise a native offset bake.");
    }

    private static void AssertAlnConversionUnavailable(string path, string facePath) =>
        WpfTestDispatcher.Invoke(() => RunWithDispatcher(async () =>
        {
            using var reader = new MorphFacePackageReader();
            using var shell = CreateMainWindowViewModel(reader);
            await shell.OpenDroppedFileAsync(path);
            shell.SelectedFace = shell.Faces.Single(face => face.InstancedPath == facePath);
            TestAssert.True(!shell.ConvertMorphCommand.CanExecute(null),
                "An unloaded ALN face offered approximate morph conversion.");
            await ((AsyncRelayCommand)shell.LoadSelectedFaceCommand).ExecuteAsync();
            TestAssert.True(shell.Editor is not null, shell.ErrorMessage ?? "The ALN face did not load.");
            TestAssert.Equal(0, shell.Editor!.Features.Count);
            TestAssert.True(!shell.ConvertMorphCommand.CanExecute(null),
                "A loaded ALN face offered approximate morph conversion.");
        }));

    private static void AssertPreviewPositions(IReadOnlyList<Vector3> expected,
        IReadOnlyList<MorphFaceEditor.Rendering.HeadPreviewVertex> actual)
    {
        TestAssert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
            TestAssert.Near(new Vector3(expected[index].Y, expected[index].Z, expected[index].X), actual[index].Position, 0);
    }

    private static void AssertAuthoredAln(MorphFaceDocument expected, MorphFaceDocument actual)
    {
        TestAssert.True(expected.MorphFeatures.SequenceEqual(actual.MorphFeatures), "ALN authored features changed.");
        TestAssert.True(expected.FinalSkeleton.SequenceEqual(actual.FinalSkeleton), "ALN authored skeleton changed.");
        TestAssert.Equal(expected.BakedLods.Count, actual.BakedLods.Count);
        for (var lod = 0; lod < expected.BakedLods.Count; lod++)
            TestAssert.True(expected.BakedLods[lod].SequenceEqual(actual.BakedLods[lod]), $"ALN authored LOD {lod} changed.");
        TestAssert.True(expected.MaterialOverrides.Scalars.OrderBy(value => value.Name)
            .SequenceEqual(actual.MaterialOverrides.Scalars.OrderBy(value => value.Name)), "ALN scalar overrides changed.");
        TestAssert.True(expected.MaterialOverrides.Vectors.OrderBy(value => value.Name)
            .SequenceEqual(actual.MaterialOverrides.Vectors.OrderBy(value => value.Name)), "ALN colour overrides changed.");
        TestAssert.True(expected.MaterialOverrides.Textures.OrderBy(value => value.Name)
            .Select(value => (value.Name, value.TextureReference?.InstancedPath))
            .SequenceEqual(actual.MaterialOverrides.Textures.OrderBy(value => value.Name)
                .Select(value => (value.Name, value.TextureReference?.InstancedPath))), "ALN texture references changed.");
    }

    private static void MalformedAndInertTargetsStayHidden()
    {
        var profiles = MorphFaceProfileRegistry.CreateDefault().Profiles;
        var male = profiles.Single(value => value.Key == "le3-human-male").GetUiProfile();
        foreach (var name in new[] { "eyes_Large", "eyeShape_droop", "eyeShape_sleepy" })
            TestAssert.True(!ProfileFeature(male, name, metadataOnly: true).IsVisible,
                $"Inert metadata {name} became editable.");

        var female = profiles.Single(value => value.Key == "le3-human-female").GetUiProfile();
        var target = TestFixtures.CreateTarget();
        var malformedHair = new MorphTargetAsset(target.Source,
            [new MorphTargetLod(0, 3, []),
             new MorphTargetLod(1, 3, [new MorphVertexDelta(0, Vector3.UnitX * 0.02f, Vector3.Zero)])], []);
        foreach (var name in new[] { "HAIR_centerPart", "HAIR_pulledBackBig", "HAIR_pulledBackSlick",
                     "HAIR_sidePart", "HAIR_slickWidowsPeak", "HAIR_splitSide" })
            TestAssert.True(!ProfileFeature(female, name, target: malformedHair).IsVisible,
                $"Malformed LE3 hair target {name} became editable.");
    }

    private static MorphFeatureMetadata ProfileFeature(IHeadEditorUiProfile profile, string name,
        bool metadataOnly = false, MorphTargetAsset? target = null) =>
        profile.Describe(new ResolvedMorphFeature(new MorphFeatureValue(name, 0),
            metadataOnly ? null : target ?? TestFixtures.CreateTarget(),
            metadataOnly ? MorphFeatureResolutionKind.MetadataOnly : MorphFeatureResolutionKind.DirectTarget, null), true);
}
