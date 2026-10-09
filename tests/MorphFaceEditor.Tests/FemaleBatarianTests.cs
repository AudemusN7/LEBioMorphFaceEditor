using System.Numerics;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.Tests;

internal static class FemaleBatarianTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("BAF corpus previews the female base and exports authored data in all games", PreviewAndExport),
        new("BAF conversion retains the destination female mesh across all game directions", ConvertAcrossGames)
    ];

    private static string Corpus(MorphFaceGame game) =>
        Path.GetFullPath(Path.Combine("tests", "Global Morphs", "BAF", $"BAF {game}.pcc"));

    private static void PreviewAndExport()
    {
        var registry = MorphFaceProfileRegistry.CreateDefault();
        foreach (var game in new[] { MorphFaceGame.LE1, MorphFaceGame.LE2, MorphFaceGame.LE3 })
        {
            var path = Corpus(game);
            var catalog = new MorphFaceCatalogService(registry).ReadAsync(path).GetAwaiter().GetResult();
            var bafFaces = catalog.Faces.Where(face => face.ProfileKey.EndsWith("-female-batarian")).ToArray();
            TestAssert.True(bafFaces.Length > 0, "BAF corpus faces were not recognised.");
            foreach (var reference in MorphFaceReferenceInspector.Inspect(path))
                TestAssert.Equal(reference.BaseHeadPath?.Contains("BAF_HED_PROBase_MDL", StringComparison.OrdinalIgnoreCase) == true,
                    registry.Find(game, reference.FacePath, reference.BaseHeadPath)?.ExportTag == "[BAF]");
            using var loader = new MorphFacePreviewLoadService(new HeadPreviewSceneFactory(), new MorphTargetCatalog(),
                registry, new MorphFacePackageReader());
            var preview = loader.LoadAsync(path, bafFaces[0].InstancedPath).GetAwaiter().GetResult();
            var loaded = preview.Loaded;
            var session = preview.EditingSession;
            var scene = preview.Scene;
            TestAssert.True(!session.CanEdit && !session.CanEditBones,
                "BAF exposed inherited geometry editing.");
            var mesh = scene.Meshes.First(mesh => !mesh.IsAttachment);
            TestAssert.True(!mesh.ApplySkinning, "BAF preview applied the inherited male skeleton.");
            TestAssert.Equal(loaded.BaseHead.Positions.Length, mesh.Vertices.Count);
            var remap = new Matrix4x4(0, 0, 1, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1);
            for (var i = 0; i < mesh.Vertices.Count; i++)
                TestAssert.True(Vector3.Distance(Vector3.Transform(loaded.BaseHead.Positions[i], remap),
                    mesh.Vertices[i].Position) < 0.0001f, "BAF preview did not use the female base vertices.");

            var draft = session.CreateDraft(null, [], loaded.Document.MaterialOverrides);
            var output = Path.Combine(Path.GetTempPath(), $"MFE-BAF-export-{game}-{Guid.NewGuid():N}.pcc");
            try
            {
                var saved = new MorphFacePackageWriter().SaveMorphToPackage(draft, path, output, true);
                using var reader = new MorphFacePackageReader();
                var reopened = reader.Load(output, saved.FaceInstancedPath, decodeTextures: false);
                TestAssert.Equal(loaded.BaseHead.Source.InstancedPath, reopened.BaseHead.Source.InstancedPath);
                TestAssert.True(draft.MorphFeatures.SequenceEqual(reopened.Document.MorphFeatures) &&
                                draft.FinalSkeleton.SequenceEqual(reopened.Document.FinalSkeleton),
                    "BAF export changed inherited authoring data.");
                TestAssert.True(draft.BakedLods.Count == reopened.Document.BakedLods.Count &&
                                draft.BakedLods.Zip(reopened.Document.BakedLods).All(pair => pair.First.SequenceEqual(pair.Second)),
                    "BAF export changed the stored bake.");
            }
            finally { if (File.Exists(output)) File.Delete(output); }
        }
    }

    private static void ConvertAcrossGames()
    {
        var profiles = MorphFaceProfileRegistry.CreateDefault();
        var service = new MorphFaceConversionService(profiles, new MorphTargetCatalog(),
            new MorphFacePackageContextService(), TestFixtures.GetCorpusTextureCatalogService());
        var games = new[] { MorphFaceGame.LE1, MorphFaceGame.LE2, MorphFaceGame.LE3 };
        foreach (var sourceGame in games)
        foreach (var targetGame in games.Where(game => game != sourceGame))
        {
            var source = new MorphFaceCatalogService(profiles).ReadAsync(Corpus(sourceGame)).GetAwaiter().GetResult().Faces
                .First(face => face.ProfileKey.EndsWith("-female-batarian"));
            var output = Path.Combine(Path.GetTempPath(), $"MFE-BAF-convert-{Guid.NewGuid():N}.pcc");
            try
            {
                var result = service.Convert(new MorphFaceConversionRequest(Corpus(sourceGame), source.InstancedPath,
                    targetGame, output, true, Corpus(targetGame)));
                using var reader = new MorphFacePackageReader();
                var reopened = reader.Load(output, result.SaveResult.FaceInstancedPath, decodeTextures: false);
                TestAssert.Equal(targetGame, reopened.Game);
                TestAssert.True(reopened.BaseHead.Source.InstancedPath.Contains("BAF_HED_PROBase_MDL"),
                    "BAF conversion substituted the male mesh.");
                TestAssert.Equal($"{targetGame.ToString().ToLowerInvariant()}-female-batarian",
                    profiles.Require(targetGame, reopened.Document.Source.InstancedPath, reopened.BaseHead.Source.InstancedPath).Key);
                TestAssert.Equal(0, result.TransferredFeatureCount);
            }
            finally { if (File.Exists(output)) File.Delete(output); }
        }
    }
}
