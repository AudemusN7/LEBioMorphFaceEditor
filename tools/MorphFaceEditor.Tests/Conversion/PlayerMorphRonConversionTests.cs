using System.Numerics;
using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Services;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.Tests;

internal static class PlayerMorphRonConversionTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("Player RON converts morphed female LE2 to LE3 with bone residual", () => ConvertMorphed(MorphFaceGame.LE2, MorphFaceGame.LE3, StandalonePlayerSex.Female)),
        new("Player RON converts morphed male LE3 to LE1 with bone residual", () => ConvertMorphed(MorphFaceGame.LE3, MorphFaceGame.LE1, StandalonePlayerSex.Male)),
        new("Player RON rejects LOD0 sculpt and unsafe destinations", RejectUnsupportedGeometryAndDestinations),
        new("Player RON opt-in transfers female LE1 baked surface once", ApproximateBakedShape)
    ];

    private static readonly Vector3 BoneOffset = new(0.125f, -0.25f, 0.375f);
    private const float MorphWeight = 0.35f;

    private static HeadMorphPayload NeutralHead(LoadedMorphFace seed, string boneName)
    {
        var head = MorphFacePackageContextService.CreateDefaultHead(seed);
        return head with { MorphData = head.MorphData with
        {
            BakedLods = seed.BaseHead.Lods.Select(lod => lod.Positions.ToArray()).ToArray(),
            FinalSkeleton = head.MorphData.FinalSkeleton.Select(bone =>
                string.Equals(bone.BoneName, boneName, StringComparison.OrdinalIgnoreCase)
                    ? bone with { Translation = bone.Translation + BoneOffset } : bone).ToArray()
        } };
    }

    private static void ConvertMorphed(MorphFaceGame sourceGame, MorphFaceGame targetGame, StandalonePlayerSex sex)
    {
        var service = PlayerConversionTestFixtures.CreateService(sourceGame, targetGame);
        var directory = Path.Combine(Path.GetTempPath(), $"MFE-PlayerRonTest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var reader = new MorphFacePackageReader();
            var source = PlayerConversionTestFixtures.LoadSeed(reader, sourceGame, sex);
            var target = PlayerConversionTestFixtures.LoadSeed(reader, targetGame, sex);
            var profiles = MorphFaceProfileRegistry.CreateDefault();
            var sourceProfile = profiles.Require(sourceGame, source.Document.Source.InstancedPath, source.Document.BaseHeadReference?.InstancedPath);
            var targetProfile = profiles.Require(targetGame, target.Document.Source.InstancedPath, target.Document.BaseHeadReference?.InstancedPath);
            var catalog = new MorphTargetCatalog();
            static string Name(MorphTargetAsset value) => value.Source.InstancedPath.Split('.').Last();
            var targetTargets = catalog.Load(targetProfile).GroupBy(Name, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);
            var sourceTarget = catalog.Load(sourceProfile)
                .Where(value => targetTargets.ContainsKey(Name(value)) &&
                    !sourceProfile.MetadataOnlyFeatures.Contains(Name(value)) && !targetProfile.MetadataOnlyFeatures.Contains(Name(value)) &&
                    !sourceProfile.FeatureAliases.ContainsKey(Name(value)) && !targetProfile.FeatureAliases.ContainsKey(Name(value)) &&
                    value.Lods.Any(lod => lod.LodIndex == 0 && lod.Vertices.Count > 0) &&
                    targetTargets[Name(value)].Lods.Any(lod => lod.LodIndex == 0 && lod.Vertices.Count > 0))
                .OrderByDescending(value => value.BoneOffsets.Count + targetTargets[Name(value)].BoneOffsets.Count)
                .First();
            var feature = Name(sourceTarget);
            var targetTarget = targetTargets[feature];
            var sourceWeighted = new[] { new WeightedMorphTarget(feature, sourceTarget, MorphWeight) };
            var targetWeighted = new[] { new WeightedMorphTarget(feature, targetTarget, MorphWeight) };
            var targetBones = target.BaseHead.Topology.ReferenceSkeleton.ToDictionary(b => b.Name, StringComparer.OrdinalIgnoreCase);
            var bone = source.BaseHead.Topology.ReferenceSkeleton
                .Where(b => targetBones.ContainsKey(b.Name))
                .OrderByDescending(b => targetTarget.BoneOffsets.Any(offset => string.Equals(offset.BoneName, b.Name, StringComparison.OrdinalIgnoreCase)))
                .First();
            var additive = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase) { [bone.Name] = BoneOffset };
            var head = NeutralHead(source, bone.Name);
            head = head with { MorphData = head.MorphData with
            {
                MorphFeatures = [new MorphFeatureValue(feature, MorphWeight)],
                BakedLods = source.BaseHead.Lods.Select(lod => SparseMorphEvaluator.EvaluatePositions(lod.Positions, sourceWeighted, lod.LodIndex)).ToArray(),
                FinalSkeleton = MorphBoneOffsetComposer.Compose(source.BaseHead.Topology.ReferenceSkeleton,
                    head.MorphData.FinalSkeleton, sourceWeighted, additive)
            } };
            TestAssert.True(head.MorphData.BakedLods[0].Where((position, index) =>
                Vector3.Distance(position, source.BaseHead.Lods[0].Positions[index]) > 0.0002f).Any(),
                "Selected feature did not produce a meaningful source deformation.");
            TestAssert.True(source.BaseHead.Lods.Any(lod => lod.LodIndex == 2) &&
                !sourceTarget.Lods.Any(lod => lod.LodIndex == 2) && !targetTarget.Lods.Any(lod => lod.LodIndex == 2),
                "Fixture did not exercise the absent target LOD2 case.");
            if (sex == StandalonePlayerSex.Female)
            {
                var nativePlayer = new MorphFacePackageContextService().CapturePlayerDefaultHead(sourceGame, sex);
                head = nativePlayer with { MorphData = head.MorphData };
                TestAssert.True(head.HairMesh != "None" || head.AccessoryMeshes.Count > 0 ||
                    head.MaterialData.Textures.Count > 0 || head.MaterialData.Scalars.Count > 0 || head.MaterialData.Vectors.Count > 0,
                    "Native Player fixture has no materials or attachments to exercise.");
            }
            // Player files commonly author only LOD0; destination lower LODs are regenerated.
            head = head with { MorphData = head.MorphData with { BakedLods = [head.MorphData.BakedLods[0]] } };
            using var imported = new StandalonePlayerMorphImportService().ImportPlayerHead(sourceGame, head, "PlayerConversionTest");
            var before = File.ReadAllBytes(imported.Workspace.WorkingPath);
            var output = Path.Combine(directory, "Converted.ron");
            var result = service.ConvertPlayerRon(new(imported.Workspace.WorkingPath, imported.ImportedFacePath, targetGame, output));
            TestAssert.Equal(output, result.DestinationPath);
            TestAssert.True(before.SequenceEqual(File.ReadAllBytes(imported.Workspace.WorkingPath)), "Conversion changed the source package.");

            var document = TseHeadMorphRon.ReadDocument(output);
            TestAssert.Equal(new RonExportProvenance(RonExportProducer.MFE, targetGame,
                sex == StandalonePlayerSex.Male ? "HMM" : "HMF", true), document.Provenance);
            var converted = document.Head.MorphData;
            TestAssert.Near(MorphWeight, converted.MorphFeatures.Single(value =>
                string.Equals(value.Name, feature, StringComparison.OrdinalIgnoreCase)).Offset, 0.000001f);
            TestAssert.Equal(target.BaseHead.Lods.Count, converted.BakedLods.Count);
            for (var lod = 0; lod < target.BaseHead.Lods.Count; lod++)
            {
                var baseLod = target.BaseHead.Lods[lod];
                var expected = SparseMorphEvaluator.EvaluatePositions(baseLod.Positions, targetWeighted, baseLod.LodIndex);
                TestAssert.Equal(expected.Length, converted.BakedLods[lod].Length);
                for (var vertex = 0; vertex < expected.Length; vertex++)
                    TestAssert.Near(expected[vertex], converted.BakedLods[lod][vertex], 0.0002f);
            }
            var expectedBones = MorphBoneOffsetComposer.Compose(target.BaseHead.Topology.ReferenceSkeleton,
                converted.FinalSkeleton, targetWeighted, additive);
            foreach (var expectedBone in expectedBones)
                TestAssert.Near(expectedBone.Translation, converted.FinalSkeleton.Single(b =>
                    string.Equals(b.BoneName, expectedBone.BoneName, StringComparison.OrdinalIgnoreCase)).Translation, 0.0002f);

        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void RejectUnsupportedGeometryAndDestinations()
    {
        var service = PlayerConversionTestFixtures.CreateService(MorphFaceGame.LE2, MorphFaceGame.LE3);
        var directory = Path.Combine(Path.GetTempPath(), $"MFE-PlayerRonReject-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var reader = new MorphFacePackageReader();
            var seed = PlayerConversionTestFixtures.LoadSeed(reader, MorphFaceGame.LE2, StandalonePlayerSex.Female);
            var head = NeutralHead(seed, seed.BaseHead.Topology.ReferenceSkeleton[0].Name);
            head = head with { MorphData = head.MorphData with { BakedLods = [head.MorphData.BakedLods[0]] } };
            head.MorphData.BakedLods[0][0] += new Vector3(0, 0.1f, 0);
            using var imported = new StandalonePlayerMorphImportService().ImportPlayerHead(MorphFaceGame.LE2, head, "PlayerResidualTest");
            var source = imported.Workspace.WorkingPath;
            var before = File.ReadAllBytes(source);
            var output = Path.Combine(directory, "Rejected.ron");
            ExpectError("loc_PlayerConversion_UnsupportedGeometry", () => service.ConvertPlayerRon(new(source, imported.ImportedFacePath, MorphFaceGame.LE3, output)));
            TestAssert.True(!File.Exists(output), "Unsupported geometry produced a RON.");
            var existing = Path.Combine(directory, "Existing.ron");
            File.WriteAllText(existing, "keep me");
            ExpectError("loc_PlayerConversion_InvalidDestination", () => service.ConvertPlayerRon(new(source, imported.ImportedFacePath, MorphFaceGame.LE3, existing)));
            TestAssert.Equal("keep me", File.ReadAllText(existing));
            var invalid = Path.Combine(directory, "Invalid.pcc");
            ExpectError("loc_PlayerConversion_InvalidDestination", () => service.ConvertPlayerRon(new(source, imported.ImportedFacePath, MorphFaceGame.LE3, invalid)));
            TestAssert.True(!File.Exists(invalid) && before.SequenceEqual(File.ReadAllBytes(source)), "Rejection changed the source or created output.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void ApproximateBakedShape()
    {
        const MorphFaceGame sourceGame = MorphFaceGame.LE1, targetGame = MorphFaceGame.LE3;
        var textures = PlayerConversionTestFixtures.Prerequisites(sourceGame, targetGame);
        var directory = Path.Combine(Path.GetTempPath(), $"MFE-PlayerApproximate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var service = new MorphFaceConversionService(MorphFaceProfileRegistry.CreateDefault(), new MorphTargetCatalog(),
                new MorphFacePackageContextService(), textures, new PlayerMorphBaselineCatalog(Path.Combine(directory, "empty")));
            using var reader = new MorphFacePackageReader();
            var source = PlayerConversionTestFixtures.LoadSeed(reader, sourceGame, StandalonePlayerSex.Female);
            var target = PlayerConversionTestFixtures.LoadSeed(reader, targetGame, StandalonePlayerSex.Female);
            var profiles = MorphFaceProfileRegistry.CreateDefault();
            var sourceProfile = profiles.Require(sourceGame, source.Document.Source.InstancedPath, source.Document.BaseHeadReference?.InstancedPath);
            var targetProfile = profiles.Require(targetGame, target.Document.Source.InstancedPath, target.Document.BaseHeadReference?.InstancedPath);
            var catalog = new MorphTargetCatalog();
            static string Name(MorphTargetAsset t) => t.Source.InstancedPath.Split('.').Last();
            var targetNames = catalog.Load(targetProfile).Select(Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var morph = catalog.Load(sourceProfile).First(t => targetNames.Contains(Name(t)) &&
                !sourceProfile.MetadataOnlyFeatures.Contains(Name(t)) && !sourceProfile.FeatureAliases.ContainsKey(Name(t)) &&
                t.Lods.Any(l => l.LodIndex == 0 && l.Vertices.Count > 0));
            var feature = Name(morph);
            var weighted = new[] { new WeightedMorphTarget(feature, morph, MorphWeight) };
            var shared = target.BaseHead.Topology.ReferenceSkeleton.Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var bone = source.BaseHead.Topology.ReferenceSkeleton.First(b => shared.Contains(b.Name) && b.Name.Equals("jawBone", StringComparison.OrdinalIgnoreCase));
            var additive = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase) { [bone.Name] = BoneOffset };
            var head = NeutralHead(source, bone.Name);
            var native = SparseMorphEvaluator.EvaluatePositions(source.BaseHead.Positions, weighted, 0);
            var finalBones = MorphBoneOffsetComposer.Compose(source.BaseHead.Topology.ReferenceSkeleton,
                head.MorphData.FinalSkeleton, weighted, additive);
            head = head with { MorphData = head.MorphData with { MorphFeatures = [new(feature, MorphWeight)], FinalSkeleton = finalBones } };
            var displacement = new Vector3(0.04f, -0.06f, 0.08f);
            // Invalid authored data must never reach a guessed mapping.
            var invalidSkeleton = source.Document with { FinalSkeleton = [new BoneTranslation("not-a-real-bone", Vector3.Zero)] };
            var invalidVertices = source.Document.BakedLods.Select(l => l.ToArray()).ToArray();
            invalidVertices[0][0] = new Vector3(float.NaN, 0, 0);
            var invalidGeometry = source.Document with { BakedLods = invalidVertices };
            static void RejectInvalid(Action action)
            {
                try { action(); }
                catch (InvalidDataException) { return; }
                throw new Exception("Invalid input was accepted by the baked shape transfer.");
            }
            RejectInvalid(() => PlayerBakedShapeTransfer.Transfer(source.BaseHead, invalidSkeleton, target.BaseHead, target.Document));
            RejectInvalid(() => PlayerBakedShapeTransfer.Transfer(source.BaseHead, invalidGeometry, target.BaseHead, target.Document));
            Vector3[]? firstPosed = null;
            for (var pass = 1; pass <= 2; pass++)
            {
                var positions = native.Select(v => v + displacement * pass).ToArray();
                var payload = head with { MorphData = head.MorphData with { BakedLods = [positions] } };
                using var imported = new StandalonePlayerMorphImportService().ImportPlayerHead(sourceGame, payload, $"Approximate{pass}");
                var path = imported.Workspace.WorkingPath;
                var before = File.ReadAllBytes(path);
                var rejected = Path.Combine(directory, $"rejected-{pass}.ron");
                ExpectError("loc_PlayerConversion_UnsupportedGeometry", () => service.ConvertPlayerRon(new(path, imported.ImportedFacePath, targetGame, rejected)));
                TestAssert.True(!File.Exists(rejected), "Default conversion published unsupported geometry.");
                var output = Path.Combine(directory, $"approximate-{pass}.ron");
                service.ConvertPlayerRon(new(path, imported.ImportedFacePath, targetGame, output, ApproximateBakedShape: true));
                TestAssert.True(before.SequenceEqual(File.ReadAllBytes(path)), "Approximation changed its source package.");
                var result = TseHeadMorphRon.ReadDocument(output);
                TestAssert.Equal(new RonExportProvenance(RonExportProducer.MFE, targetGame, "HMF", true), result.Provenance);
                TestAssert.Near(MorphWeight, result.Head.MorphData.MorphFeatures.Single(f => f.Name.Equals(feature, StringComparison.OrdinalIgnoreCase)).Offset, 0.000001f);
                var targetMorph = catalog.Load(targetProfile).Single(t => Name(t).Equals(feature, StringComparison.OrdinalIgnoreCase));
                var expectedBone = target.BaseHead.Topology.ReferenceSkeleton.Single(b => b.Name.Equals(bone.Name, StringComparison.OrdinalIgnoreCase)).Position + BoneOffset +
                    targetMorph.BoneOffsets.Where(b => b.BoneName.Equals(bone.Name, StringComparison.OrdinalIgnoreCase))
                        .Aggregate(Vector3.Zero, (sum, value) => sum + value.Offset * MorphWeight);
                TestAssert.Near(expectedBone, result.Head.MorphData.FinalSkeleton.Single(b => b.BoneName.Equals(bone.Name, StringComparison.OrdinalIgnoreCase)).Translation, 0.0004f);
                var lod = target.BaseHead.Lods[0];
                TestAssert.Equal(lod.Positions.Length, result.Head.MorphData.BakedLods[0].Length);
                var pose = SkeletalPoseComposer.Compose(lod.Topology.ReferenceSkeleton, result.Head.MorphData.FinalSkeleton);
                var posed = CpuSkinningEvaluator.Skin(new DeformationResult(result.Head.MorphData.BakedLods[0], lod.Normals, 0, []), lod.RenderData!, pose.SkinningMatrices).Positions;
                if (firstPosed is not null)
                    for (var i = 0; i < posed.Length; i++)
                        TestAssert.Near(displacement, posed[i] - firstPosed[i], 0.0004f);
                firstPosed = posed;
                using var reimport = new StandalonePlayerMorphImportService().ImportPlayerRon(targetGame, output, "ApproximateReopen");
                var reopened = new MorphFacePackageContextService().CaptureHead(reimport.Workspace.WorkingPath, reimport.ImportedFacePath);
                TestAssert.True(result.Head.MorphData.BakedLods[0].SequenceEqual(reopened.MorphData.BakedLods[0]), "Reimport changed approximate LOD0.");
            }
            var male = PlayerConversionTestFixtures.LoadSeed(reader, sourceGame, StandalonePlayerSex.Male);
            var maleHead = MorphFacePackageContextService.CreateDefaultHead(male);
            var malePositions = male.BaseHead.Positions.Select(v => v + displacement).ToArray();
            maleHead = maleHead with { MorphData = maleHead.MorphData with { BakedLods = [malePositions] } };
            using var maleImport = new StandalonePlayerMorphImportService().ImportPlayerHead(sourceGame, maleHead, "ApproximateMaleRejected");
            var maleBefore = File.ReadAllBytes(maleImport.Workspace.WorkingPath);
            var maleOutput = Path.Combine(directory, "male-rejected.ron");
            ExpectError("loc_PlayerConversion_UnsupportedGeometry", () => service.ConvertPlayerRon(new(
                maleImport.Workspace.WorkingPath, maleImport.ImportedFacePath, targetGame, maleOutput, ApproximateBakedShape: true)));
            TestAssert.True(!File.Exists(maleOutput) && maleBefore.SequenceEqual(File.ReadAllBytes(maleImport.Workspace.WorkingPath)),
                "Unsupported male approximation changed its input or published output.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void ExpectError(string key, Action action)
    {
        try { action(); }
        catch (Exception exception)
        {
            TestAssert.Equal(key, UserFacingMessage.FromException(exception)?.LocalizationKey);
            return;
        }
        throw new Exception($"Expected {key}.");
    }
}
