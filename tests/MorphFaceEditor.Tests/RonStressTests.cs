using System.Numerics;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.Tests;

/// <summary>
/// Evidence tests for the Trilogy Save Editor RON stress corpus. The fixtures
/// stay outside the test output and are read in place from the repository.
/// </summary>
internal static class RonStressTests
{
    private const int ExpectedCorpusFileCount = 34;

    private static readonly (string FileName, string VariantPrefix, MorphFaceGame[] Games, StandalonePlayerSex Sex, int Lod0Vertices)[] PrimaryCases =
    [
        ("LE1-2_Customizable_Sheploo.ron", "LE1-2_Customizable_Sheploo_", [MorphFaceGame.LE1, MorphFaceGame.LE2], StandalonePlayerSex.Male, 2294),
        ("LE1-2_Default_Jane_iconichair.ron", "LE1-2_Default_Jane_", [MorphFaceGame.LE1, MorphFaceGame.LE2], StandalonePlayerSex.Female, 2232),
        ("LE3_Customizable_Sheploo.ron", "LE3_Customizable_Sheploo_", [MorphFaceGame.LE3], StandalonePlayerSex.Male, 2392),
        ("LE3_HMF_HED_Iconic_HairIconic.ron", "LE3_HMF_HED_Iconic_", [MorphFaceGame.LE3], StandalonePlayerSex.Female, 2390)
    ];

    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("RON stress corpus parses all 34 files and verifies variant structure", ParseCorpus),
        new("RON texture catalogue canonicalises occurrence package paths", CanonicalTextureCataloguePaths),
        new("RON stress primary roots classify for their intended games", ClassifyPrimaryRoots),
        new("RON stress standalone imports preserve primary payloads and installed fingerprints", StandalonePrimaryRoundTrips)
    ];

    private static void ParseCorpus()
    {
        var directory = CorpusDirectory();
        var files = Directory.GetFiles(directory, "*.ron", SearchOption.AllDirectories)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        TestAssert.Equal(ExpectedCorpusFileCount, files.Length);

        var parsed = files.ToDictionary(value => Path.GetFileName(value), TseHeadMorphRon.Read,
            StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var ron = parsed[Path.GetFileName(file)];
            var counts = ron.MorphData.BakedLods.Select(value => value.Length).ToArray();
            TestAssert.True(counts.Length is >= 1 and <= 4,
                $"{Path.GetFileName(file)} did not contain a contiguous LOD sequence.");
            TestAssert.True(counts.All(value => value > 0),
                $"{Path.GetFileName(file)} contained an empty baked LOD.");
            TestAssert.True(ron.MorphData.MorphFeatures.Count > 0,
                $"{Path.GetFileName(file)} did not contain morph features.");
            TestAssert.True(ron.MorphData.FinalSkeleton.Count > 0,
                $"{Path.GetFileName(file)} did not contain offset bones.");
            TestAssert.True(ron.MaterialData.Textures.All(value =>
                    value.TextureReference is null ||
                    !string.IsNullOrWhiteSpace(value.TextureReference.InstancedPath)),
                $"{Path.GetFileName(file)} retained an empty texture path instead of a null reference.");
            Console.WriteLine($"RON {Path.GetFileName(file)}: LOD counts [{string.Join(", ", counts)}], " +
                              $"features={ron.MorphData.MorphFeatures.Count}, bones={ron.MorphData.FinalSkeleton.Count}, " +
                              $"scalars={ron.MaterialData.Scalars.Count}, vectors={ron.MaterialData.Vectors.Count}, " +
                              $"textures={ron.MaterialData.Textures.Count}, hair={ron.HairMesh}, " +
                              $"accessories={ron.AccessoryMeshes.Count}");
        }

        foreach (var primary in PrimaryCases)
        {
            var root = parsed[primary.FileName];
            var rootCounts = root.MorphData.BakedLods.Select(value => value.Length).ToArray();
            var variants = parsed.Where(pair => pair.Key.StartsWith(primary.VariantPrefix,
                StringComparison.OrdinalIgnoreCase)).ToArray();
            TestAssert.True(variants.Length > 0, $"{primary.FileName} did not match any attachment variants.");
            foreach (var variant in variants)
            {
                var variantCounts = variant.Value.MorphData.BakedLods.Select(value => value.Length).ToArray();
                TestAssert.True(rootCounts.SequenceEqual(variantCounts),
                    $"{variant.Key} changed baked LOD counts from {primary.FileName}: " +
                    $"[{string.Join(", ", rootCounts)}] -> [{string.Join(", ", variantCounts)}].");
                TestAssert.True(variant.Value.MorphData.MorphFeatures.Count > 0,
                    $"{variant.Key} lost its morph-feature payload.");
                TestAssert.True(variant.Value.MorphData.FinalSkeleton.Count > 0,
                    $"{variant.Key} lost its final-skeleton payload.");
            }

            TestAssert.True(rootCounts.SequenceEqual([primary.Lod0Vertices]),
                $"{primary.FileName} has unexpected baked LOD counts: [{string.Join(", ", rootCounts)}].");
        }
    }

    private static void CanonicalTextureCataloguePaths()
    {
        const string internalPath = "PROCustomFade01.HMM_HIR_PROCustomFade01_Mask";
        const string requestedPath = "BIOG_HMM_HIR_PRO_R.PROCustomFade01.HMM_HIR_PROCustomFade01_Mask";
        var occurrence = new TextureCatalogOccurrence(
            Path.Combine("C:\\Games\\LE1", "BIOG_HMM_HIR_PRO_R.pcc"),
            123,
            0,
            TextureCatalogOrigin.BaseGame,
            512,
            512,
            "PF_DXT1",
            "TEXTUREGROUP_Character",
            false,
            null);
        var candidate = new TextureCatalogCandidate(
            TextureCatalogGame.LE1,
            internalPath,
            occurrence,
            [occurrence]);

        TestAssert.True(StandalonePlayerAssetCatalog.MatchesRequestedTexturePath(
                candidate, MorphFaceGame.LE1, requestedPath),
            "The registry's internal export path was not canonicalised through its occurrence package.");
        TestAssert.True(!StandalonePlayerAssetCatalog.MatchesRequestedTexturePath(
                candidate, MorphFaceGame.LE2, requestedPath),
            "Cross-game registry occurrence was admitted.");
        TestAssert.True(!StandalonePlayerAssetCatalog.MatchesRequestedTexturePath(
                candidate, MorphFaceGame.LE1,
                "BIOG_OTHER.PROCustomFade02.HMM_HIR_PROCustomFade01_Mask"),
            "An unrelated package root was admitted by object name.");
        TestAssert.True(!StandalonePlayerAssetCatalog.MatchesRequestedTexturePath(
                candidate, MorphFaceGame.LE1,
                "BIOG_HMM_HIR_PRO_R.PROCustomFade02.HMM_HIR_PROCustomFade01_Mask"),
            "A malformed RON parent path was silently repaired by the exact catalogue lookup.");
    }

    private static void ClassifyPrimaryRoots()
    {
        var directory = CorpusDirectory();
        foreach (var primary in PrimaryCases)
        {
            var path = Path.Combine(directory, primary.FileName);
            var ron = TseHeadMorphRon.Read(path);
            TestAssert.Equal(primary.Lod0Vertices, ron.MorphData.BakedLods[0].Length);
            foreach (var game in primary.Games)
            {
                var actual = StandalonePlayerMorphImportService.IdentifySex(
                    game, ron.MorphData.BakedLods[0].Length);
                TestAssert.Equal(primary.Sex, actual);
                Console.WriteLine($"RON classification {primary.FileName} -> {game}/{actual} " +
                                  $"(LOD0 vertices={ron.MorphData.BakedLods[0].Length})");
            }

            foreach (var game in new[] { MorphFaceGame.LE1, MorphFaceGame.LE2, MorphFaceGame.LE3 }
                         .Except(primary.Games))
            {
                var rejected = false;
                try
                {
                    _ = StandalonePlayerMorphImportService.IdentifySex(
                        game, ron.MorphData.BakedLods[0].Length);
                }
                catch (InvalidDataException)
                {
                    rejected = true;
                }
                TestAssert.True(rejected,
                    $"{primary.FileName} was incorrectly accepted for incompatible selected game {game}.");
            }
        }
    }

    private static void StandalonePrimaryRoundTrips()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var directory = CorpusDirectory();
        var service = new StandalonePlayerMorphImportService();
        var context = new MorphFacePackageContextService();
        var attempted = 0;
        var completed = 0;
        var failures = new List<string>();

        var roundTripCases = PrimaryCases.Append((
            Path.Combine("variants", "LE1-2_Default_Jane_ashleyhair.ron"),
            string.Empty,
            [MorphFaceGame.LE1, MorphFaceGame.LE2],
            StandalonePlayerSex.Female,
            2232));
        foreach (var primary in roundTripCases)
        {
            var ronPath = Path.Combine(directory, primary.FileName);
            var expected = TseHeadMorphRon.Read(ronPath);
            foreach (var game in primary.Games)
            {
                string seedPath;
                try
                {
                    seedPath = StandalonePlayerMorphImportService.ResolveInstalledSeed(game);
                }
                catch (FileNotFoundException exception)
                {
                    Console.WriteLine($"SKIP {primary.FileName} -> {game}: {exception.Message}");
                    continue;
                }
                catch (DirectoryNotFoundException exception)
                {
                    Console.WriteLine($"SKIP {primary.FileName} -> {game}: {exception.Message}");
                    continue;
                }

                attempted++;
                var readbackPath = Path.Combine(Path.GetTempPath(),
                    $"MFE-RonStress-{game}-{Guid.NewGuid():N}.ron");
                Dictionary<string, PackageFingerprint>? installedFingerprints = null;
                try
                {
                    // Build only exact installed candidates for this RON. Passing an
                    // empty registry candidate set makes ForRon exercise its named
                    // installed-package and seed-reference fallback paths too.
                    var assets = StandalonePlayerAssetCatalog.ForRon(game, ronPath, []);
                    installedFingerprints = new[] { seedPath }
                        .Concat(assets.Textures.Select(value => value.PackagePath))
                        .Concat(assets.SkeletalMeshes.Select(value => value.PackagePath))
                        .Where(File.Exists)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(value => value, PackageFingerprint.Capture,
                            StringComparer.OrdinalIgnoreCase);
                    using var imported = service.ImportPlayerRon(
                        game, ronPath, $"RonStress_{game}_{primary.Sex}", assets);

                    TestAssert.Equal(game, imported.Game);
                    TestAssert.Equal(primary.Sex, imported.Sex);
                    if (primary.FileName.Contains("LE1-2_Customizable_Sheploo", StringComparison.OrdinalIgnoreCase))
                    {
                        TestAssert.True(imported.SaveResult.Warnings.Any(value =>
                                value.Contains("HMM_HIR_PROCustomFade01_Mask", StringComparison.OrdinalIgnoreCase)),
                            $"{game} Sheploo silently rewrote or discarded its malformed authored texture path.");
                    }
                    if (primary.FileName.Contains("ashleyhair", StringComparison.OrdinalIgnoreCase))
                    {
                        TestAssert.True(imported.SaveResult.Warnings.All(value =>
                                !value.Contains("malformed Texture2D", StringComparison.OrdinalIgnoreCase)),
                            $"{game} Ashley hair retained its empty HAIR_Mask sentinel as a malformed texture.");
                    }
                    TestAssert.True(!imported.CanCommit && !imported.Workspace.CanCommit,
                        $"{game} {primary.FileName} exposed a commit-capable workspace.");
                    AssertMorphEqual(expected.MorphData,
                        context.CaptureMorphData(imported.Workspace.WorkingPath, imported.ImportedFacePath),
                        $"{game} {primary.FileName} imported morph data");
                    AssertMaterialEqual(expected.MaterialData,
                        context.CaptureMaterialData(imported.Workspace.WorkingPath, imported.ImportedFacePath),
                        $"{game} {primary.FileName} imported material data");
                    MorphFaceMorphData editedMorph;
                    using (var reader = new MorphFacePackageReader())
                    {
                        var loaded = reader.Load(imported.Workspace.WorkingPath, imported.ImportedFacePath);
                        TestAssert.True(loaded.TopologyDiagnostics.IsValid,
                            $"{game} {primary.FileName} did not produce a renderable LOD0 preview.");
                        AssertMorphEqual(expected.MorphData, loaded.Document is { } document
                                ? new MorphFaceMorphData(document.MorphFeatures, document.FinalSkeleton, document.BakedLods)
                                : throw new Exception("Preview document was unavailable."),
                            $"{game} {primary.FileName} preview payload");
                        editedMorph = CreateRelativeMorphEdit(
                            loaded,
                            imported.Workspace.WorkingPath,
                            expected.MorphData,
                            $"{game} {primary.FileName}");
                    }

                    context.ExportRon(imported.Workspace.WorkingPath, imported.ImportedFacePath, readbackPath);
                    var readback = TseHeadMorphRon.Read(readbackPath);
                    AssertMorphEqual(expected.MorphData, readback.MorphData,
                        $"{game} {primary.FileName} RON readback morph data");
                    AssertMaterialEqual(expected.MaterialData, readback.MaterialData,
                        $"{game} {primary.FileName} RON readback material data");
                    TestAssert.Equal(NormalizeAttachment(expected.HairMesh), NormalizeAttachment(readback.HairMesh));
                    TestAssert.True(expected.AccessoryMeshes.SequenceEqual(readback.AccessoryMeshes,
                            StringComparer.OrdinalIgnoreCase),
                        $"{game} {primary.FileName} changed accessory references during RON readback.");

                    context.PasteMorphData(
                        imported.Workspace.WorkingPath,
                        imported.ImportedFacePath,
                        editedMorph);
                    AssertMorphEqual(
                        editedMorph,
                        context.CaptureMorphData(imported.Workspace.WorkingPath, imported.ImportedFacePath),
                        $"{game} {primary.FileName} persisted relative morph edit");
                    File.Delete(readbackPath);
                    context.ExportRon(imported.Workspace.WorkingPath, imported.ImportedFacePath, readbackPath);
                    AssertMorphEqual(
                        editedMorph,
                        TseHeadMorphRon.Read(readbackPath).MorphData,
                        $"{game} {primary.FileName} exported relative morph edit");
                    completed++;
                }
                catch (UnauthorizedAccessException exception) when (IsWorkspacePermissionBlock(exception))
                {
                    // The service intentionally places detached workspaces in
                    // LocalApplicationData. In restricted CI containers that
                    // location may be readable but not writable; report this as
                    // an environment skip, not as a conversion result.
                    Console.WriteLine($"SKIP {primary.FileName} -> {game}: detached workspace path is not writable ({exception.Message})");
                }
                catch (Exception exception)
                {
                    var failure = $"{primary.FileName} -> {game}: {exception.Message}";
                    failures.Add(failure);
                    Console.WriteLine($"STRESS FAILURE {failure}");
                }
                finally
                {
                    if (File.Exists(readbackPath)) File.Delete(readbackPath);
                    if (installedFingerprints is not null)
                    {
                        foreach (var (path, fingerprint) in installedFingerprints)
                        {
                            TestAssert.Equal(fingerprint, PackageFingerprint.Capture(path));
                        }
                    }
                }
            }
        }

        if (completed == 0)
        {
            Console.WriteLine(attempted == 0
                ? "SKIP RON stress standalone round trips: no installed LE player seed packages were found."
                : "SKIP RON stress standalone round trips: installed seed packages were found but detached workspace storage was unavailable.");
        }
        if (failures.Count > 0)
        {
            throw new Exception("RON stress imports failed:\n- " + string.Join("\n- ", failures));
        }
    }

    private static void AssertMorphEqual(MorphFaceMorphData expected, MorphFaceMorphData actual, string label)
    {
        TestAssert.Equal(expected.MorphFeatures.Count, actual.MorphFeatures.Count);
        TestAssert.Equal(expected.FinalSkeleton.Count, actual.FinalSkeleton.Count);
        TestAssert.Equal(expected.BakedLods.Count, actual.BakedLods.Count);
        for (var index = 0; index < expected.MorphFeatures.Count; index++)
        {
            TestAssert.Equal(expected.MorphFeatures[index], actual.MorphFeatures[index]);
        }
        for (var index = 0; index < expected.FinalSkeleton.Count; index++)
        {
            TestAssert.Equal(expected.FinalSkeleton[index], actual.FinalSkeleton[index]);
        }
        for (var lod = 0; lod < expected.BakedLods.Count; lod++)
        {
            TestAssert.Equal(expected.BakedLods[lod].Length, actual.BakedLods[lod].Length);
            for (var vertex = 0; vertex < expected.BakedLods[lod].Length; vertex++)
            {
                if (Vector3.Distance(expected.BakedLods[lod][vertex], actual.BakedLods[lod][vertex]) > 0.000001f)
                {
                    throw new Exception($"{label} changed baked LOD {lod} vertex {vertex}.");
                }
            }
        }
    }

    private static MorphFaceMorphData CreateRelativeMorphEdit(
        LoadedMorphFace loaded,
        string workspacePath,
        MorphFaceMorphData expected,
        string label)
    {
        var profiles = MorphFaceProfileRegistry.CreateDefault();
        var resolution = profiles.Resolve(
            loaded.Game,
            loaded.Document.Source.InstancedPath,
            loaded.Document.BaseHeadReference?.InstancedPath,
            loaded.Document.MaterialOverrides,
            loaded.Materials) ?? throw new Exception($"{label} did not resolve a Player profile.");
        TestAssert.True(!resolution.UsesCustomMesh,
            $"{label} was classified as a custom-mesh profile instead of canonical Player topology.");
        var profile = resolution.Profile;
        var session = new MorphFaceEditingSession(
            loaded.Document,
            loaded.BaseHead,
            new MorphTargetCatalog().Load(profile, loaded.Game, workspacePath),
            profile.MetadataOnlyFeatures,
            profile.DisplayName,
            profile.FeatureAliases,
            profile.RecognizesBaseVariant,
            profile.GeometryEditBlockReason(loaded.BaseHead.Source.InstancedPath),
            geometryMode: MorphFaceGeometryMode.RelativeBake);

        TestAssert.True(session.CanEditMorphFeatures,
            $"{label} did not expose safe relative morph editing: {session.EditBlockReason}");
        TestAssert.True(session.CanEditBones,
            $"{label} lost its independently verified Player bone controls.");
        TestAssert.True(session.Evaluation.Geometry.Positions.SequenceEqual(expected.BakedLods[0]),
            $"{label} changed its iconic authored bake merely by entering relative mode.");

        var expectedAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Jaw_Width"] = "jaw_wide",
            ["mouthShape_thick"] = "mouthShape_fatLips"
        };
        foreach (var (authoredName, targetName) in expectedAliases.Where(alias =>
                     expected.MorphFeatures.Any(feature =>
                         string.Equals(feature.Name, alias.Key, StringComparison.OrdinalIgnoreCase))))
        {
            var resolved = session.Evaluation.Resolution.Features.Single(feature =>
                string.Equals(feature.Feature.Name, authoredName, StringComparison.OrdinalIgnoreCase));
            TestAssert.True(
                resolved.Kind == MorphFaceEditor.Core.Deformation.MorphFeatureResolutionKind.AliasTarget &&
                string.Equals(
                    resolved.Target?.Source.InstancedPath.Split('.').Last(),
                    targetName,
                    StringComparison.OrdinalIgnoreCase),
                $"{label} did not resolve creator feature '{authoredName}' to canonical target '{targetName}'.");
        }

        var feature = session.Evaluation.Resolution.Features.FirstOrDefault(value =>
            value.Target?.Lods.FirstOrDefault(lod => lod.LodIndex == 0)?.Vertices.Any(vertex =>
                vertex.PositionDelta.LengthSquared() > 1e-12f) == true)
            ?? throw new Exception($"{label} did not expose a canonical LOD0 position target.");
        var targetLod = feature.Target!.Lods.First(lod => lod.LodIndex == 0);
        var changedVertex = targetLod.Vertices.First(vertex => vertex.PositionDelta.LengthSquared() > 1e-12f);
        var before = expected.BakedLods[0][changedVertex.SourceIndex];
        session.SetFeature(feature.Feature.Name, feature.Feature.Offset + 0.125f);
        var after = session.Evaluation.Geometry.Positions[changedVertex.SourceIndex];
        TestAssert.Near(before + changedVertex.PositionDelta * 0.125f, after, 0.00001f);
        var edited = session.CreateDraft(
            loaded.Document.HairMeshReference,
            loaded.Document.OtherMeshReferences,
            loaded.Document.MaterialOverrides);
        session.Undo();
        TestAssert.True(session.Evaluation.Geometry.Positions.SequenceEqual(expected.BakedLods[0]),
            $"{label} did not restore the exact authored bake after undoing a relative slider edit.");
        session.Redo();
        var redone = session.CreateDraft(
            loaded.Document.HairMeshReference,
            loaded.Document.OtherMeshReferences,
            loaded.Document.MaterialOverrides);
        AssertMorphEqual(
            new MorphFaceMorphData(edited.MorphFeatures, edited.FinalSkeleton, edited.BakedLods),
            new MorphFaceMorphData(redone.MorphFeatures, redone.FinalSkeleton, redone.BakedLods),
            $"{label} relative morph redo");
        return new MorphFaceMorphData(edited.MorphFeatures, edited.FinalSkeleton, edited.BakedLods);
    }

    private static void AssertMaterialEqual(MorphFaceMaterialData expected, MorphFaceMaterialData actual,
        string label)
    {
        TestAssert.Equal(expected.Scalars.Count, actual.Scalars.Count);
        TestAssert.Equal(expected.Vectors.Count, actual.Vectors.Count);
        TestAssert.Equal(expected.Textures.Count, actual.Textures.Count);
        for (var index = 0; index < expected.Scalars.Count; index++)
        {
            TestAssert.Equal(expected.Scalars[index], actual.Scalars[index]);
        }
        for (var index = 0; index < expected.Vectors.Count; index++)
        {
            TestAssert.Equal(expected.Vectors[index], actual.Vectors[index]);
        }
        for (var index = 0; index < expected.Textures.Count; index++)
        {
            TestAssert.Equal(expected.Textures[index].Name, actual.Textures[index].Name);
            var expectedPath = expected.Textures[index].TextureReference?.InstancedPath;
            var actualPath = actual.Textures[index].TextureReference?.InstancedPath;
            TestAssert.True(string.Equals(expectedPath, actualPath, StringComparison.OrdinalIgnoreCase),
                $"{label} changed texture map entry '{expected.Textures[index].Name}' from " +
                $"'{expectedPath}' to '{actualPath}'.");
        }
    }

    private static string NormalizeAttachment(string path) =>
        string.IsNullOrWhiteSpace(path) || path.Equals("None", StringComparison.OrdinalIgnoreCase)
            ? "None"
            : path;

    private static bool IsWorkspacePermissionBlock(UnauthorizedAccessException exception) =>
        exception.Message.Contains("Workspaces", StringComparison.OrdinalIgnoreCase);

    private static string CorpusDirectory()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "tests", "RON Stress Test");
            if (Directory.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("The tests/RON Stress Test corpus was not found.");
    }
}
