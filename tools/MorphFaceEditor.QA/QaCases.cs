using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Rendering;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.QA;

internal static partial class QaCases
{
    private static IReadOnlyList<QaCase>? _matrix;
    internal static IReadOnlyList<QaCase> All => _matrix ??= BuildMatrix();

    private static async Task Load(QaContext c)
    {
        var source = c.Source();
        c.Current.Evidence["sourceFace"] = source;
        var preview = await c.Load(source.PackagePath, source.FacePath);
        QaContext.Require(preview.Profile.Key.EndsWith("-" + c.Face.ProfileSuffix, StringComparison.OrdinalIgnoreCase),
            "Representative resolved to the wrong archetype.");
        await c.Render(preview.Scene, "loaded.png");
    }

    private static async Task Conversion(QaContext c, MorphFaceGame targetGame)
    {
        var source = c.Source();
        var target = c.CorpusPackage(targetGame);
        var destination = c.Artifact($"{source.Game}-to-{targetGame}.pcc");
        var service = new MorphFaceConversionService(c.Profiles, c.Targets, new MorphFacePackageContextService(), c.Textures);
        var result = await c.Measure("cross-game conversion", () => Task.Run(() => service.Convert(
            new MorphFaceConversionRequest(source.PackagePath, source.FacePath, targetGame,
                destination, true, target))), QaStageKind.FileTransfer);
        c.Current.Evidence["conversion"] = result;
        var reopened = await c.Load(destination, result.SaveResult.FaceInstancedPath);
        QaContext.Require(reopened.Loaded.Game == targetGame, "Converted package has the wrong game.");
        QaContext.Require(reopened.Profile.Key == $"{targetGame.ToString().ToLowerInvariant()}-{c.Face.ProfileSuffix}", "Conversion resolved to the wrong archetype.");
        QaContext.Require((source.HairMeshPath is not null) == (reopened.Loaded.Document.HairMeshReference is not null) &&
            source.OtherMeshPaths.Count == reopened.Loaded.Document.OtherMeshReferences.Count,
            "Conversion changed the curated source's attachment presence/count.");
        await c.Render(reopened.Scene, "converted.png");
    }

    private static async Task NpcRandomisation(QaContext c)
    {
        var source = c.Source();
        var preview = await c.Load(source.PackagePath, source.FacePath);
        using var scope = await c.Editor(preview, source.PackagePath);
        await RollAndExport(c, preview, scope.Editor, source.PackagePath, "randomised.pcc");
    }

    private static async Task PlayerRandomisation(QaContext c)
    {
        var game = c.Face.Game;
        var seeded = c.Source();
        var seedPackage = seeded.PackagePath;
        var ron = c.Artifact("player-input.ron");
        await c.Measure("Player RON export", () => Task.Run(() =>
            new MorphFacePackageContextService().ExportRon(seedPackage, seeded.FacePath, ron)), QaStageKind.FileTransfer);
        var registry = await c.Textures.ReadAsync(game);
        if (!registry.IsAvailable) throw new QaPrerequisiteException($"{game} asset database must be Ready for Player import.");
        var assets = StandalonePlayerAssetCatalog.ForRon(game, ron, registry.Candidates,
            attachmentMeshes: registry.AttachmentMeshes);
        using var imported = await c.Measure("standalone Player RON import", () => Task.Run(() =>
            new StandalonePlayerMorphImportService().ImportPlayerRon(game, ron, "QaPlayer", assets)), QaStageKind.FileTransfer);
        QaContext.Require(!imported.CanCommit, "Standalone Player workspace unexpectedly permits source commit.");
        QaContext.Require(imported.Sex == c.Face.PlayerSex, "Standalone import detected the wrong Player sex.");
        var preview = await c.Load(imported.Workspace.WorkingPath, imported.ImportedFacePath, MorphFaceGeometryMode.RelativeBake);
        using var scope = await c.Editor(preview, imported.Workspace.WorkingPath, player: true);
        c.Current.Evidence["route"] = "Standalone Player RON / RelativeBake";
        await RollAndExport(c, preview, scope.Editor, seedPackage, "player-randomised.pcc", MorphFaceGeometryMode.RelativeBake);
    }

    private static async Task RollAndExport(QaContext c, MorphFacePreviewLoadResult preview,
        MorphFaceEditor.ViewModels.FaceEditorViewModel editor, string logicalSource, string filename,
        MorphFaceGeometryMode? reloadMode = null)
    {
        var before = editor.CreateDraft();
        var beforeMaterials = MaterialSignature(editor.Material.CaptureInterchangeData());
        await c.Randomise100(editor);
        var draft = editor.CreateDraft();
        QaContext.Require(!MorphFaceEditorStateComparer.Equals(before, draft), "Randomisation completed without changing authored state.");
        QaContext.Require(beforeMaterials != MaterialSignature(editor.Material.CaptureInterchangeData()),
            "Representative 100% roll changed no material values.");
        if (editor.CanEditMorphFeatures && editor.RandomiseMorphs)
            QaContext.Require(!before.MorphFeatures.SequenceEqual(draft.MorphFeatures) ||
                before.BakedLods.Zip(draft.BakedLods).Any(pair => !pair.First.SequenceEqual(pair.Second)),
                "Editable 100% roll changed materials but no morph features or baked geometry.");
        c.Current.Evidence["randomisationStrength"] = new { Morph = 100, Material = 100 };
        var result = await c.Save(draft, logicalSource, filename);
        var reopened = await c.Load(result.PackagePath, result.FaceInstancedPath, reloadMode);
        AssertGeometry(draft, reopened.Loaded.Document);
        AssertMaterials(draft, reopened.Loaded.Document);
        await c.Render(reopened.Scene, "randomised-reopened.png");
    }

    private static async Task UndoRedo(QaContext c)
    {
        var source = c.Source();
        var preview = await c.Load(source.PackagePath, source.FacePath);
        using var scope = await c.Editor(preview, source.PackagePath);
        var editor = scope.Editor;
        var initial = editor.CreateDraft();
        ApplyFirstHistoryEdit(c, editor);
        var afterMorph = editor.CreateDraft();
        await EditMaterial(c, editor, 0.01f);
        var afterMaterial = editor.CreateDraft();
        QaContext.Require(!MorphFaceEditorStateComparer.Equals(afterMorph, afterMaterial), "Material history setup was a no-op.");
        editor.UndoCommand.Execute(null);
        AssertState(afterMorph, editor.CreateDraft(), "first undo restores material only");
        editor.UndoCommand.Execute(null);
        AssertState(initial, editor.CreateDraft(), "second undo restores morph");
        editor.RedoCommand.Execute(null);
        AssertState(afterMorph, editor.CreateDraft(), "first redo restores morph");
        editor.RedoCommand.Execute(null);
        AssertState(afterMaterial, editor.CreateDraft(), "second redo restores material");
        MorphFaceDocument beforeFinalRoll = afterMaterial;
        await c.Randomise100(editor, "History randomisation", () => beforeFinalRoll = editor.CreateDraft());
        var afterRandom = editor.CreateDraft();
        editor.UndoCommand.Execute(null);
        AssertState(beforeFinalRoll, editor.CreateDraft(), "one undo reverses the final randomisation");
        editor.RedoCommand.Execute(null);
        AssertState(afterRandom, editor.CreateDraft(), "one redo restores whole randomisation");
        editor.UndoCommand.Execute(null);
        await EditMaterial(c, editor, 0.02f);
        QaContext.Require(!editor.RedoCommand.CanExecute(null), "A new edit did not discard the redo branch.");
        var saved = await c.Save(editor.CreateDraft(), source.PackagePath, "history-result.pcc");
        var reopened = await c.Load(saved.PackagePath, saved.FaceInstancedPath);
        AssertGeometry(editor.CreateDraft(), reopened.Loaded.Document);
        AssertMaterials(editor.CreateDraft(), reopened.Loaded.Document);
        c.Current.Evidence["verifiedSteps"] = new[] { "mixed chronology", "randomisation aggregate", "redo branch invalidation", "save/reopen" };
        await c.Render(reopened.Scene, "history-reopened.png");
    }

    private static async Task Benchmark(QaContext c)
    {
        var source = c.Source();
        using var reader = new MorphFacePackageReader();
        using var loader = new MorphFacePreviewLoadService(c.Scenes, c.Targets, c.Profiles, reader);
        var timings = new List<double>();
        MorphFacePreviewLoadResult? preview = null;
        for (var index = 0; index < 4; index++)
        {
            preview = await c.Measure(index == 0 ? "Cold load" : $"Warm load {index}",
                () => loader.LoadAsync(source.PackagePath, source.FacePath, geometryMode: c.Face.GeometryMode),
                index == 0 ? QaStageKind.ColdLoad : QaStageKind.Load);
            timings.Add(c.Current.Stages[^1].DurationMs);
            c.Current.Evidence[$"loadMetrics{index}"] = reader.LastLoadMetrics;
        }
        using var scope = await c.Editor(preview!, source.PackagePath);
        await c.Randomise100(scope.Editor);
        var saved = await c.Save(scope.Editor.CreateDraft(), source.PackagePath, "benchmark-result.pcc");
        var reopened = await c.Load(saved.PackagePath, saved.FaceInstancedPath);
        await c.Render(reopened.Scene, "benchmark-reopened.png");
        c.Current.Evidence["coldReaderMs"] = timings[0];
        c.Current.Evidence["warmMedianMs"] = timings.Skip(1).Order().ElementAt(1);
        c.Current.Evidence["benchmarkScope"] = "Cold means a new reader, not a flushed OS disk cache. Three warm load samples and 100 randomisation rolls; export uses the final outcome.";
        c.Current.Evidence["slowestStages"] = c.Current.Stages.OrderByDescending(value => value.DurationMs).Take(3).ToArray();
    }

    private static void AssertGeometry(MorphFaceDocument expected, MorphFaceDocument actual)
    {
        QaContext.Require(expected.MorphFeatures.SequenceEqual(actual.MorphFeatures), "Saved morph features differ.");
        QaContext.Require(expected.FinalSkeleton.Count == actual.FinalSkeleton.Count && expected.FinalSkeleton.Zip(actual.FinalSkeleton)
            .All(pair => pair.First.BoneName == pair.Second.BoneName && Vector3.Distance(pair.First.Translation, pair.Second.Translation) <= 0.00001f),
            "Saved skeleton differs.");
        QaContext.Require(expected.BakedLods.Count == actual.BakedLods.Count, "Saved LOD count differs.");
        for (var index = 0; index < expected.BakedLods.Count; index++)
            QaContext.Require(expected.BakedLods[index].Length == actual.BakedLods[index].Length &&
                expected.BakedLods[index].Zip(actual.BakedLods[index]).All(pair => Vector3.Distance(pair.First, pair.Second) <= 0.00001f),
                $"Saved LOD {index} geometry differs.");
    }

    private static void AssertState(MorphFaceDocument expected, MorphFaceDocument actual, string step)
    {
        QaContext.Require(MorphFaceEditorStateComparer.Equals(expected, actual), $"History failed: {step}.");
        AssertGeometry(expected, actual);
    }

    private static void AssertMaterials(MorphFaceDocument expected, MorphFaceDocument actual)
    {
        static MorphFaceMaterialData Data(MorphFaceDocument document) => new(document.MaterialOverrides.Scalars,
            document.MaterialOverrides.Vectors, document.MaterialOverrides.Textures);
        QaContext.Require(MaterialSignature(Data(expected)) == MaterialSignature(Data(actual)),
            "Saved material override values or texture identities differ after fresh reopening.");
    }

    private static string MaterialSignature(MorphFaceMaterialData data)
    {
        using var cache = new LegendaryExplorerCore.Packages.PackageCache { CacheMaxSize = 8 };
        var normalized = new
        {
            Scalars = data.Scalars.OrderBy(value => value.Name), Vectors = data.Vectors.OrderBy(value => value.Name),
            Textures = data.Textures.OrderBy(value => value.Name).Select(value => new
                { value.Name, Path = MaterialIdentity(value.TextureReference, cache), Class = value.TextureReference?.ClassName.ToUpperInvariant() })
        };
        return JsonSerializer.Serialize(normalized, new JsonSerializerOptions { IncludeFields = true });
    }

    private static string? MaterialIdentity(AssetIdentity? reference, LegendaryExplorerCore.Packages.PackageCache cache)
    {
        if (reference is null) return null;
        if (reference.IsImport || !File.Exists(reference.PackagePath)) return reference.InstancedPath.ToUpperInvariant();
        var package = cache.GetCachedPackage(reference.PackagePath);
        QaContext.Require(package is not null && package.IsUExport(reference.UIndex),
            "Material comparison cannot locate its exact export occurrence: " + reference);
        var export = package!.GetUExport(reference.UIndex);
        QaContext.Require(export.ClassName.Equals(reference.ClassName, StringComparison.OrdinalIgnoreCase) &&
            (export.InstancedFullPath.Equals(reference.InstancedPath, StringComparison.OrdinalIgnoreCase) ||
             PccAssetPathPolicy.FromExport(export).Equals(reference.InstancedPath, StringComparison.OrdinalIgnoreCase)),
            "Material comparison found an incorrect export identity: " + reference);
        return PccAssetPathPolicy.FromExport(export).ToUpperInvariant();
    }

    private static string[] MaterialDifferences(MorphFaceMaterialData expected, MorphFaceMaterialData actual)
    {
        using var cache = new LegendaryExplorerCore.Packages.PackageCache { CacheMaxSize = 8 };
        var differences = new List<string>();
        void Compare<T>(IEnumerable<T> left, IEnumerable<T> right, Func<T, string> name,
            Func<T, string> value, string kind)
        {
            var desired = left.ToDictionary(name, value, StringComparer.Ordinal);
            var restored = right.ToDictionary(name, value, StringComparer.Ordinal);
            foreach (var key in desired.Keys.Union(restored.Keys).Order())
            {
                var before = desired.GetValueOrDefault(key, "<missing parameter>");
                var after = restored.GetValueOrDefault(key, "<missing parameter>");
                if (before != after) differences.Add($"{kind} '{key}': expected {before}; actual {after}.");
            }
        }
        Compare(expected.Scalars, actual.Scalars, value => value.Name,
            value => value.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture), "Scalar");
        Compare(expected.Vectors, actual.Vectors, value => value.Name,
            value => JsonSerializer.Serialize(value.Value, new JsonSerializerOptions { IncludeFields = true }), "Vector");
        Compare(expected.Textures, actual.Textures, value => value.Name,
            value => value.TextureReference is { } reference
                ? $"{reference.ClassName.ToUpperInvariant()} {MaterialIdentity(reference, cache)}"
                : "None", "Texture");
        return differences.ToArray();
    }
}
