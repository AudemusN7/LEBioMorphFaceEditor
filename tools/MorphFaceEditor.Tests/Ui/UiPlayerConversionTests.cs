using LegendaryExplorerCore.Unreal;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Core.Services;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.LegendaryExplorer.TextureRegistry;
using MorphFaceEditor.Services;
using static MorphFaceEditor.Tests.UiTestFixtures;
using static MorphFaceEditor.Tests.PlayerConversionTestFixtures;

namespace MorphFaceEditor.Tests;

internal static class UiPlayerConversionTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("Player conversion cancellation preserves RON career and new drafts", PlayerSourcesCancel),
        new("Player conversion includes unsaved edits without saving the source", PlayerConversionIncludesUnsavedEdits),
        new("conversion eligibility preserves PCC, NPC and detached mesh boundaries", OtherWorkspaceEligibility)
    ];

    private static void PlayerSourcesCancel() => RunWithDispatcher(async () =>
    {
        RequireSeeds(MorphFaceGame.LE2, MorphFaceGame.LE3);
        var directory = Path.Combine(Path.GetTempPath(), $"MFE-PlayerConvertUi-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var ron = Path.Combine(directory, "Player.ron");
            var game = MorphFaceGame.LE2;
            TseHeadMorphRon.Write(ron, new MorphFacePackageContextService().CapturePlayerDefaultHead(game, StandalonePlayerSex.Female),
                new RonExportProvenance(RonExportProducer.MFE, game, "HMF", true));
            var career = Path.Combine(directory, "Career.pcsav");
            File.Copy(PcsavHeadMorphTests.Fixtures().Single(path => Path.GetFileName(path) == "LE3-Male-Custom.pcsav"), career);
            foreach (var source in new string?[] { ron, career, null })
            {
                var dialogs = new StubEditorDialogs { StandaloneGameChoiceResult = game, StandaloneNameChoiceResult = "Player",
                    NewPlayerMorphChoiceResult = new(game, StandalonePlayerSex.Female) };
                using var reader = new MorphFacePackageReader();
                using var shell = CreateMainWindowViewModel(reader, dialogs);
                if (source is null) await ((AsyncRelayCommand)shell.NewPlayerMorphCommand).ExecuteAsync();
                else await shell.OpenDroppedFileAsync(source);
                var editor = shell.Editor ?? throw new Exception(shell.ErrorMessage ?? "Player source did not open.");
                editor.Material.Scalars.First().Value += 0.125f;
                var working = editor.CreateDraft().Source.PackagePath;
                var beforeWorking = PackageFingerprint.Capture(working);
                var beforeSource = source is null ? null : File.ReadAllBytes(source);
                TestAssert.True(shell.IsDirty && shell.ConvertMorphCommand.CanExecute(null), "Player conversion was unavailable with unsaved edits.");
                await ((AsyncRelayCommand)shell.ConvertMorphCommand).ExecuteAsync();
                TestAssert.Equal(1, dialogs.PlayerConversionChoiceCount);
                if (source is not null) TestAssert.Equal(source, dialogs.PlayerConversionSourcePath!);
                TestAssert.True(shell.IsDirty && editor.IsDirty && (source is null || beforeSource!.SequenceEqual(File.ReadAllBytes(source))),
                    "Cancelling conversion changed the source or cleared edits.");
                TestAssert.Equal(beforeWorking, PackageFingerprint.Capture(working));
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    });

    private static void PlayerConversionIncludesUnsavedEdits() => RunWithDispatcher(async () =>
    {
        var catalogue = Prerequisites(MorphFaceGame.LE2, MorphFaceGame.LE3);
        var directory = Path.Combine(Path.GetTempPath(), $"MFE-PlayerConvertSuccess-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "Source.ron");
            var destination = Path.Combine(directory, "Converted.ron");
            using var reader = new MorphFacePackageReader();
            var seed = LoadSeed(reader, MorphFaceGame.LE2, StandalonePlayerSex.Female);
            var native = new MorphFacePackageContextService().CapturePlayerDefaultHead(MorphFaceGame.LE2, StandalonePlayerSex.Female);
            var neutral = MorphFacePackageContextService.CreateDefaultHead(seed);
            var head = neutral with { MaterialData = native.MaterialData, MorphData = neutral.MorphData with
            {
                BakedLods = [seed.BaseHead.Lods[0].Positions.ToArray()]
            } };
            TseHeadMorphRon.Write(source, head, new(RonExportProducer.MFE, MorphFaceGame.LE2, "HMF", true));
            var original = File.ReadAllBytes(source);
            var dialogs = new StubEditorDialogs { StandaloneGameChoiceResult = MorphFaceGame.LE2, StandaloneNameChoiceResult = "Player",
                PlayerConversionChoiceResult = new(MorphFaceGame.LE3, destination, true, null) };
            using var shell = CreateMainWindowViewModel(reader, dialogs, catalogue);
            await shell.OpenDroppedFileAsync(source);
            var editor = shell.Editor ?? throw new Exception(shell.ErrorMessage ?? "Player source did not open.");
            var sourceWorkingPath = editor.CreateDraft().Source.PackagePath;
            var sourceLod0 = editor.CreateDraft().BakedLods[0].ToArray();
            var targetSeed = LoadSeed(reader, MorphFaceGame.LE3, StandalonePlayerSex.Female);
            var targetBones = targetSeed.BaseHead.Topology.ReferenceSkeleton.ToDictionary(bone => bone.Name, StringComparer.OrdinalIgnoreCase);
            var bone = editor.Bones.First(axis => axis.IsAvailable && targetBones.ContainsKey(axis.BoneName) &&
                seed.BaseHead.Topology.ReferenceSkeleton.Any(item => string.Equals(item.Name, axis.BoneName, StringComparison.OrdinalIgnoreCase)));
            var boneName = bone.BoneName;
            var boneAxis = bone.Axis;
            var oldBoneValue = bone.Value;
            bone.BeginEdit();
            bone.Value += 0.125f;
            bone.EndEdit();
            var boneOffset = bone.Value - oldBoneValue;
            TestAssert.True(Math.Abs(boneOffset) > 0.001f, "The bone control did not change the source bone.");
            TestAssert.True(sourceLod0.SequenceEqual(editor.CreateDraft().BakedLods[0]),
                "Editing a bone changed the authored LOD0 bake.");
            var skinTone = editor.Material.Vectors.Single(value => value.Name == "SkinTone");
            var expected = skinTone.Value with { X = skinTone.Value.X + 0.125f };
            var materials = editor.CreateDraft().MaterialOverrides;
            await editor.ApplyMaterialDataAsync(new(materials.Scalars,
                [.. materials.Vectors.Where(value => value.Name != "SkinTone"), new VectorMaterialOverride("SkinTone", expected)],
                materials.Textures));
            await ((AsyncRelayCommand)shell.ConvertMorphCommand).ExecuteAsync();
            TestAssert.True(!shell.HasError && File.Exists(destination), shell.ErrorMessage ?? "Player conversion did not create a RON.");
            var converted = TseHeadMorphRon.ReadDocument(destination);
            TestAssert.True(converted.Provenance is { PlayerMorph: true, Game: MorphFaceGame.LE3 }, "Output lost its Player/game provenance.");
            TestAssert.Equal(targetSeed.BaseHead.Lods.Count, converted.Head.MorphData.BakedLods.Count);
            TestAssert.True(converted.Head.MorphData.BakedLods.Count > 1 && converted.Head.MorphData.BakedLods
                .Select((lod, index) => lod.Length == targetSeed.BaseHead.Lods[index].Positions.Length).All(matches => matches),
                "Conversion did not generate the target's complete LOD geometry.");
            var targetReference = targetBones[boneName].Position;
            var targetBone = converted.Head.MorphData.FinalSkeleton.Single(item =>
                string.Equals(item.BoneName, boneName, StringComparison.OrdinalIgnoreCase)).Translation;
            float AxisValue(System.Numerics.Vector3 value) => boneAxis switch { 0 => value.X, 1 => value.Y, _ => value.Z };
            TestAssert.Near(AxisValue(targetReference) + boneOffset, AxisValue(targetBone), 0.0001f);
            var flushed = new MorphFacePackageContextService().CaptureHead(sourceWorkingPath, shell.SelectedFace!.InstancedPath);
            using (var workspacePackage = LegendaryExplorerCore.Packages.MEPackageHandler.OpenMEPackage(sourceWorkingPath, forceLoadFromDisk: true))
                TestAssert.True(!workspacePackage.IsCompressed, "The disposable Player workspace was compressed during the flush.");
            TestAssert.True(sourceLod0.SequenceEqual(flushed.MorphData.BakedLods[0]),
                "Flushing the bone edit changed the source's authored LOD0 bake.");
            TestAssert.Near(oldBoneValue + boneOffset, AxisValue(flushed.MorphData.FinalSkeleton.Single(item =>
                string.Equals(item.BoneName, boneName, StringComparison.OrdinalIgnoreCase)).Translation), 0.0001f);
            var actual = converted.Head.MaterialData.Vectors.Single(value => value.Name == "SkinTone").Value;
            TestAssert.Near(expected.X, actual.X, 0.00001f);
            TestAssert.Near(expected.Y, actual.Y, 0.00001f);
            TestAssert.Near(expected.Z, actual.Z, 0.00001f);
            TestAssert.Near(expected.W, actual.W, 0.00001f);
            TestAssert.True(original.SequenceEqual(File.ReadAllBytes(source)), "Conversion saved over the source RON.");
            TestAssert.True(shell.IsDirty, "Conversion cleared the source's pending changes.");
            TestAssert.True(shell.Editor?.CreateDraft().Source.PackagePath == sourceWorkingPath &&
                shell.SelectedFace?.ProfileKey == "le2-human-female",
                "Conversion replaced the source workspace with its output.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    });

    private static void OtherWorkspaceEligibility() => RunWithDispatcher(async () =>
    {
        var catalogue = new TextureCatalogService(new TextureRegistryStore(TextureRegistryPaths.CreateDefault()));
        var assets = await catalogue.ReadAsync(MorphFaceGame.LE3);
        if (!assets.IsAvailable) throw new TestSkippedException("The installed LE3 texture registry is required.");
        using (var reader = new MorphFacePackageReader())
        using (var shell = CreateMainWindowViewModel(reader, new StubEditorDialogs {
            NewNpcMorphChoiceResult = new(MorphFaceGame.LE3, "HMM"), StandaloneNameChoiceResult = "NewNpc" }, catalogue))
        {
            await ((AsyncRelayCommand)shell.NewNpcMorphCommand).ExecuteAsync();
            TestAssert.True(shell.Editor is not null, shell.ErrorMessage ?? "New NPC did not open.");
            TestAssert.True(!shell.ConvertMorphCommand.CanExecute(null), "Detached NPC offered conversion.");
        }
        var pcc = Path.GetFullPath(Path.Combine("fixtures", "Global Morphs", "LE1 GlobalMorphs.pcc"));
        if (!File.Exists(pcc)) throw new TestSkippedException("The local PCC fixture is not present.");
        using (var reader = new MorphFacePackageReader())
        using (var shell = CreateMainWindowViewModel(reader))
        {
            await shell.OpenDroppedFileAsync(pcc);
            shell.SelectedFace = shell.Faces.First(face => !face.ProfileKey.EndsWith("-vorcha", StringComparison.OrdinalIgnoreCase));
            TestAssert.True(shell.ConvertMorphCommand.CanExecute(null), "Existing PCC conversion became unavailable.");
        }
        var mesh = Path.Combine(Path.GetTempPath(), $"MFE-ConvertMesh-{Guid.NewGuid():N}.psk");
        try
        {
            new PSK { Points = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)],
                Wedges = [new() { PointIndex = 0 }, new() { PointIndex = 1 }, new() { PointIndex = 2 }],
                Faces = [new() { WedgeIdx0 = 0, WedgeIdx1 = 1, WedgeIdx2 = 2, MatIndex = 0 }],
                Materials = [new() { Name = "Head" }], Bones = [], Weights = [], VertexNormals = [] }.ToFile(mesh);
            using var reader = new MorphFacePackageReader();
            using var shell = CreateMainWindowViewModel(reader, new StubEditorDialogs {
                StandaloneGameChoiceResult = MorphFaceGame.LE1, StandaloneNameChoiceResult = "DetachedMesh" });
            await shell.OpenDroppedFileAsync(mesh);
            TestAssert.True(shell.IsDetachedMeshWorkspace, shell.ErrorMessage ?? "Detached mesh did not open.");
            TestAssert.True(!shell.ConvertMorphCommand.CanExecute(null), "Detached mesh offered conversion.");
        }
        finally { if (File.Exists(mesh)) File.Delete(mesh); }
    });

}
