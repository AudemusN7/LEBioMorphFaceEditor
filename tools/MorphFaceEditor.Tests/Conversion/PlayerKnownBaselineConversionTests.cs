using System.Numerics;
using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Services;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;
using MorphFaceEditor.QA;

namespace MorphFaceEditor.Tests;

internal static class PlayerKnownBaselineConversionTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("Player known baseline LE2 to LE3 female", Convert),
        new("Player known baseline rejects an extra sculpt", RejectSculpt),
        new("Player known baseline requires its target counterpart", MissingCounterpart)
    ];

    private static void MissingCounterpart()
    {
        var textures = PlayerConversionTestFixtures.Prerequisites(MorphFaceGame.LE2, MorphFaceGame.LE3);
        using var generated = new QaSyntheticPlayerBaselines([MorphFaceGame.LE2], [StandalonePlayerSex.Female]);
        using var reader = new MorphFacePackageReader();
        var source = PlayerConversionTestFixtures.LoadSeed(reader, MorphFaceGame.LE2, StandalonePlayerSex.Female);
        var target = PlayerConversionTestFixtures.LoadSeed(reader, MorphFaceGame.LE3, StandalonePlayerSex.Female);
        var profiles = MorphFaceProfileRegistry.CreateDefault();
        var sourceProfile = profiles.Require(MorphFaceGame.LE2, source.Document.Source.InstancedPath, source.Document.BaseHeadReference?.InstancedPath);
        var targetProfile = profiles.Require(MorphFaceGame.LE3, target.Document.Source.InstancedPath, target.Document.BaseHeadReference?.InstancedPath);
        var baseline = PlayerConversionTestFixtures.RequireBaseline(generated.Catalog, sourceProfile);
        TestAssert.True(generated.Catalog.FindCounterpart(baseline.Id, targetProfile) is null, "Unexpected target baseline.");
        using var imported = new StandalonePlayerMorphImportService().ImportPlayerHead(MorphFaceGame.LE2,
            LeanHead(source, baseline.MorphData), "MissingBaselineTarget");
        var before = File.ReadAllBytes(imported.Workspace.WorkingPath);
        var output = Path.Combine(generated.Root, "rejected.ron");
        var service = new MorphFaceConversionService(profiles, new MorphTargetCatalog(),
            new MorphFacePackageContextService(), textures, playerBaselines: generated.Catalog);
        try { service.ConvertPlayerRon(new(imported.Workspace.WorkingPath, imported.ImportedFacePath, MorphFaceGame.LE3, output)); }
        catch (Exception error)
        {
            TestAssert.Equal("loc_PlayerConversion_MissingBaselineCounterpart", UserFacingMessage.FromException(error)?.LocalizationKey);
            TestAssert.True(!File.Exists(output) && before.SequenceEqual(File.ReadAllBytes(imported.Workspace.WorkingPath)),
                "Missing counterpart changed the source or created output.");
            return;
        }
        throw new Exception("Expected conversion to reject the missing counterpart.");
    }

    private static void Convert()
    {
        const MorphFaceGame sourceGame = MorphFaceGame.LE2, targetGame = MorphFaceGame.LE3;
        const StandalonePlayerSex sex = StandalonePlayerSex.Female;
        var route = $"{sourceGame} to {targetGame} {sex}";
        var textures = PlayerConversionTestFixtures.Prerequisites(sourceGame, targetGame);
        using var generated = new QaSyntheticPlayerBaselines([sourceGame, targetGame], [sex]);
        var baselines = generated.Catalog;
        var service = new MorphFaceConversionService(MorphFaceProfileRegistry.CreateDefault(), new MorphTargetCatalog(),
            new MorphFacePackageContextService(), textures, playerBaselines: baselines);
        var directory = Path.Combine(Path.GetTempPath(), $"MFE-KnownPlayer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var reader = new MorphFacePackageReader();
            var source = PlayerConversionTestFixtures.LoadSeed(reader, sourceGame, sex);
            var target = PlayerConversionTestFixtures.LoadSeed(reader, targetGame, sex);
            var profiles = MorphFaceProfileRegistry.CreateDefault();
            var sourceProfile = profiles.Require(sourceGame, source.Document.Source.InstancedPath, source.Document.BaseHeadReference?.InstancedPath);
            var targetProfile = profiles.Require(targetGame, target.Document.Source.InstancedPath, target.Document.BaseHeadReference?.InstancedPath);
            var sourceBaseline = PlayerConversionTestFixtures.RequireBaseline(baselines, sourceProfile);
            var targetBaseline = PlayerConversionTestFixtures.RequireCounterpart(baselines, sourceBaseline.Id, targetProfile);
            var catalog = new MorphTargetCatalog();
            var sourceTargets = catalog.Load(sourceProfile);
            var targetTargets = catalog.Load(targetProfile);
            var head = LeanHead(source, sourceBaseline.MorphData);
            using var imported = new StandalonePlayerMorphImportService().ImportPlayerHead(sourceGame, head, "KnownBaselineTest");
            var sourcePath = imported.Workspace.WorkingPath;

            // A zero-edit conversion must retain the actual paired face, including its authored sculpt.
            var before = File.ReadAllBytes(sourcePath);
            var constantPath = Path.Combine(directory, "constant.ron");
            service.ConvertPlayerRon(new(sourcePath, imported.ImportedFacePath, targetGame, constantPath));
            var constant = TseHeadMorphRon.ReadDocument(constantPath).Head.MorphData;
            AssertVertices(targetBaseline.MorphData.BakedLods[0], constant.BakedLods[0], route + " constant");
            TestAssert.True(before.SequenceEqual(File.ReadAllBytes(sourcePath)), route + ": constant conversion changed its input.");

            var loaded = reader.Load(sourcePath, imported.ImportedFacePath, decodeTextures: false);
            var session = new MorphFaceEditingSession(loaded.Document, loaded.BaseHead, sourceTargets,
                sourceProfile.MetadataOnlyFeatures, sourceProfile.GetDisplayName(), sourceProfile.FeatureAliases,
                sourceProfile.RecognizesBaseVariant, sourceProfile.GeometryEditBlockReason(loaded.BaseHead.Source.InstancedPath),
                geometryMode: MorphFaceGeometryMode.RelativeBake);
            TestAssert.True(session.CanEditMorphFeatures && session.CanEditBones, route + ": editing unavailable.");
            var targetNames = targetTargets.Select(Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var available = session.Features.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var feature = sourceTargets.Where(t => available.Contains(Name(t)) && targetNames.Contains(Name(t)) &&
                    !sourceProfile.MetadataOnlyFeatures.Contains(Name(t)) && !targetProfile.MetadataOnlyFeatures.Contains(Name(t)) &&
                    !sourceProfile.FeatureAliases.ContainsKey(Name(t)) && !targetProfile.FeatureAliases.ContainsKey(Name(t)) &&
                    t.Lods.Any(lod => lod.LodIndex == 0 && lod.Vertices.Count > 0))
                .OrderBy(Name, StringComparer.OrdinalIgnoreCase).First();
            var featureName = Name(feature);
            session.SetFeature(featureName, session.GetFeature(featureName) + 0.1f);
            var targetBoneNames = target.BaseHead.Topology.ReferenceSkeleton.Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var bone = session.Evaluation.FinalSkeleton.Select(b => b.BoneName)
                .Where(targetBoneNames.Contains)
                .OrderByDescending(n => feature.BoneOffsets.Any(b =>
                    string.Equals(b.BoneName, n, StringComparison.OrdinalIgnoreCase)))
                .First();
            session.SetBoneAxis(bone, 0, session.GetBoneAxis(bone, 0) + 0.02f);
            var draft = session.CreateDraft(loaded.Document.HairMeshReference, loaded.Document.OtherMeshReferences, loaded.Document.MaterialOverrides);
            new MorphFacePackageWriter().SaveExisting(draft);
            var reopened = reader.Load(sourcePath, imported.ImportedFacePath, decodeTextures: false);
            var beforeConversion = File.ReadAllBytes(sourcePath);
            var output = Path.Combine(directory, "converted.ron");
            service.ConvertPlayerRon(new(sourcePath, imported.ImportedFacePath, targetGame, output));
            TestAssert.True(beforeConversion.SequenceEqual(File.ReadAllBytes(sourcePath)), route + ": conversion changed its input.");
            var ron = TseHeadMorphRon.ReadDocument(output);
            TestAssert.Equal(new RonExportProvenance(RonExportProducer.MFE, targetGame, "HMF", true), ron.Provenance);
            var converted = ron.Head.MorphData;
            var expectedWeight = Weight(targetBaseline.MorphData.MorphFeatures, featureName) +
                Weight(reopened.Document.MorphFeatures, featureName) - Weight(sourceBaseline.MorphData.MorphFeatures, featureName);
            TestAssert.Near(expectedWeight, Weight(converted.MorphFeatures, featureName), 0.00001f);

            var sourceInitial = Resolve(sourceBaseline.MorphData.MorphFeatures, sourceTargets, sourceProfile);
            var sourceFinal = Resolve(reopened.Document.MorphFeatures, sourceTargets, sourceProfile);
            var targetInitial = Resolve(targetBaseline.MorphData.MorphFeatures, targetTargets, targetProfile);
            var targetFinal = Resolve(converted.MorphFeatures, targetTargets, targetProfile);
            var zero = new Vector3[targetBaseline.MorphData.BakedLods[0].Length];
            var initialDelta = SparseMorphEvaluator.EvaluatePositions(zero, targetInitial, 0);
            var finalDelta = SparseMorphEvaluator.EvaluatePositions(zero, targetFinal, 0);
            var expectedVertices = targetBaseline.MorphData.BakedLods[0].Select((v, i) => v + finalDelta[i] - initialDelta[i]).ToArray();
            AssertVertices(expectedVertices, converted.BakedLods[0], route);
            TestAssert.Equal(target.BaseHead.Lods.Count, converted.BakedLods.Count);
            for (var lod = 0; lod < target.BaseHead.Lods.Count; lod++)
            {
                TestAssert.Equal(target.BaseHead.Lods[lod].Positions.Length, converted.BakedLods[lod].Length);
                if (lod > 0)
                {
                    var baseLod = target.BaseHead.Lods[lod];
                    var regenerated = SparseMorphEvaluator.EvaluatePositions(baseLod.Positions, targetFinal, baseLod.LodIndex);
                    AssertVertices(regenerated, converted.BakedLods[lod], route + $" regenerated LOD{lod}");
                }
            }

            var sourceBind = source.BaseHead.Topology.ReferenceSkeleton.ToDictionary(b => b.Name, b => b.Position, StringComparer.OrdinalIgnoreCase);
            var targetBind = target.BaseHead.Topology.ReferenceSkeleton.ToDictionary(b => b.Name, b => b.Position, StringComparer.OrdinalIgnoreCase);
            var sourceBaseBone = BoneOrBind(sourceBaseline.MorphData.FinalSkeleton, sourceBind, bone);
            var targetBaseBone = BoneOrBind(targetBaseline.MorphData.FinalSkeleton, targetBind, bone);
            var manual = BoneOrBind(reopened.Document.FinalSkeleton, sourceBind, bone) - sourceBaseBone -
                (Offset(sourceFinal, bone) - Offset(sourceInitial, bone));
            var expectedBone = targetBaseBone + Offset(targetFinal, bone) - Offset(targetInitial, bone) + manual;
            TestAssert.Near(expectedBone, BoneOrBind(converted.FinalSkeleton, targetBind, bone), 0.0003f);
        }
        catch (TestSkippedException) { throw; }
        catch (Exception e) { throw new Exception($"{route}: {e.Message}", e); }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void RejectSculpt()
    {
        var textures = PlayerConversionTestFixtures.Prerequisites(MorphFaceGame.LE2, MorphFaceGame.LE3);
        using var generated = new QaSyntheticPlayerBaselines([MorphFaceGame.LE2, MorphFaceGame.LE3], [StandalonePlayerSex.Female]);
        var baselines = generated.Catalog;
        using var reader = new MorphFacePackageReader();
        var seed = PlayerConversionTestFixtures.LoadSeed(reader, MorphFaceGame.LE2, StandalonePlayerSex.Female);
        var profile = MorphFaceProfileRegistry.CreateDefault().Require(MorphFaceGame.LE2, seed.Document.Source.InstancedPath, seed.Document.BaseHeadReference?.InstancedPath);
        var baseline = PlayerConversionTestFixtures.RequireBaseline(baselines, profile).MorphData;
        var lods = baseline.BakedLods.Select(l => l.ToArray()).ToArray();
        lods[0][0] += new Vector3(0, 0.1f, 0);
        using var imported = new StandalonePlayerMorphImportService().ImportPlayerHead(MorphFaceGame.LE2,
            LeanHead(seed, baseline with { BakedLods = lods }), "KnownBaselineSculpt");
        var source = imported.Workspace.WorkingPath;
        var before = File.ReadAllBytes(source);
        var output = Path.Combine(Path.GetTempPath(), $"MFE-Rejected-{Guid.NewGuid():N}.ron");
        try
        {
            var service = new MorphFaceConversionService(MorphFaceProfileRegistry.CreateDefault(), new MorphTargetCatalog(), new MorphFacePackageContextService(), textures, playerBaselines: baselines);
            try { service.ConvertPlayerRon(new(source, imported.ImportedFacePath, MorphFaceGame.LE3, output)); }
            catch (Exception e)
            {
                TestAssert.Equal("loc_PlayerConversion_UnsupportedGeometry", UserFacingMessage.FromException(e)?.LocalizationKey);
                TestAssert.True(!File.Exists(output) && before.SequenceEqual(File.ReadAllBytes(source)), "Rejected sculpt changed an input or created output.");
                return;
            }
            throw new Exception("Expected the extra sculpt to be rejected.");
        }
        finally { if (File.Exists(output)) File.Delete(output); }
    }

    private static HeadMorphPayload LeanHead(LoadedMorphFace seed, MorphFaceMorphData data) =>
        MorphFacePackageContextService.CreateDefaultHead(seed) with
        { HairMesh = "None", AccessoryMeshes = [], MaterialData = new([], [], []), MorphData = data };

    private static string Name(MorphTargetAsset target) => target.Source.InstancedPath.Split('.').Last();
    private static float Weight(IReadOnlyList<MorphFeatureValue> features, string name) =>
        features.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))?.Offset ?? 0f;
    private static IReadOnlyList<WeightedMorphTarget> Resolve(IReadOnlyList<MorphFeatureValue> features,
        IReadOnlyList<MorphTargetAsset> targets, MorphFaceProfile profile)
    {
        // Conversion uses native target names, not CC alias rows from the fixture.
        var native = targets.Where(t => !profile.MetadataOnlyFeatures.Contains(Name(t)))
            .ToDictionary(Name, StringComparer.OrdinalIgnoreCase);
        return features.Where(f => f.Offset != 0 && native.ContainsKey(f.Name))
            .Select(f => new WeightedMorphTarget(f.Name, native[f.Name], f.Offset)).ToArray();
    }
    private static Vector3 BoneOrBind(IReadOnlyList<BoneTranslation> bones, IReadOnlyDictionary<string, Vector3> bind, string name) =>
        bones.FirstOrDefault(b => string.Equals(b.BoneName, name, StringComparison.OrdinalIgnoreCase))?.Translation ?? bind[name];
    private static Vector3 Offset(IReadOnlyList<WeightedMorphTarget> targets, string name) =>
        targets.SelectMany(t => t.Target.BoneOffsets.Where(b => string.Equals(b.BoneName, name, StringComparison.OrdinalIgnoreCase))
            .Select(b => b.Offset * t.Weight)).Aggregate(Vector3.Zero, (a, b) => a + b);
    private static void AssertVertices(IReadOnlyList<Vector3> expected, IReadOnlyList<Vector3> actual, string label)
    {
        TestAssert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
            TestAssert.True(Vector3.Distance(expected[i], actual[i]) <= 0.0004f, $"{label}: vertex {i} differs: {expected[i]} vs {actual[i]}.");
    }
}
