using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using System.Numerics;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Services;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;
using MorphFaceEditor.QA;
using static MorphFaceEditor.Tests.UiTestFixtures;
using static MorphFaceEditor.Tests.PlayerConversionTestFixtures;

namespace MorphFaceEditor.Tests;

internal static class UiKnownBaselineEditingTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("ordinary PCC known Player baselines remain editable and save", KnownBaselines),
        new("ordinary PCC extra Player sculpt remains blocked", () => BlockedBaseline(changedRig: false)),
        new("ordinary PCC changed Player rig remains loadable and blocked", () => BlockedBaseline(changedRig: true))
    ];

    private static void KnownBaselines() => RunWithDispatcher(async () =>
    {
        RequireSeeds(MorphFaceGame.LE1);
        using var generated = new QaSyntheticPlayerBaselines([MorphFaceGame.LE1], [StandalonePlayerSex.Female, StandalonePlayerSex.Male]);
        var baselines = generated.Catalog;
        var directory = NewDirectory();
        try
        {
            foreach (var sex in new[] { StandalonePlayerSex.Female, StandalonePlayerSex.Male })
            {
                using var reader = new MorphFacePackageReader();
                var head = CreateHead(reader, sex, baselines);
                using var imported = new StandalonePlayerMorphImportService().ImportPlayerHead(MorphFaceGame.LE1, head, "KnownBaselineUi");
                var source = Path.Combine(directory, $"{sex}.pcc");
                var output = Path.Combine(directory, $"{sex}-saved.pcc");
                File.Copy(imported.Workspace.WorkingPath, source);
                var bytes = File.ReadAllBytes(source);
                var original = new MorphFacePackageContextService().CaptureHead(source, imported.ImportedFacePath).MorphData;
                var dialogs = new StubEditorDialogs { SaveAsPath = output };
                using var shell = CreateMainWindowViewModel(reader, dialogs, playerBaselines: baselines);
                await OpenFace(shell, source, imported.ImportedFacePath);
                var editor = shell.Editor!;
                AssertEditable(shell, original, sex.ToString());
                var initial = editor.CreateDraft();
                var sliders = editor.Features.Where(f => f.IsEditable).Take(2).ToArray();
                TestAssert.Equal(2, sliders.Length);
                foreach (var slider in sliders)
                {
                    slider.BeginEdit();
                    slider.Value += 0.03f;
                    slider.EndEdit();
                }
                var bone = editor.Bones.First(b => b.IsAvailable);
                bone.BeginEdit();
                bone.Value += 0.02f;
                bone.EndEdit();
                var edited = editor.CreateDraft();
                TestAssert.True(!MorphFaceEditorStateComparer.Equals(initial, edited), "Controls did not change the face.");
                for (var i = 0; i < 3; i++) editor.UndoCommand.Execute(null);
                TestAssert.True(MorphFaceEditorStateComparer.Equals(initial, editor.CreateDraft()), "Undo did not restore the baseline and pose.");
                TestAssert.True(!editor.IsDirty && !shell.IsDirty && !editor.HasPendingRepair, "Undo left the original face dirty or pending repair.");
                for (var i = 0; i < 3; i++) editor.RedoCommand.Execute(null);
                TestAssert.True(MorphFaceEditorStateComparer.Equals(edited, editor.CreateDraft()), "Redo did not restore the edited face.");
                await ((AsyncRelayCommand)shell.SaveAsCommand).ExecuteAsync();
                TestAssert.True(!shell.HasError && File.Exists(output), shell.ErrorMessage ?? "Save As failed.");
                var saved = new MorphFacePackageContextService().CaptureHead(output, imported.ImportedFacePath).MorphData;
                PackageTestFixtures.AssertMorphEqual(new(edited.MorphFeatures, edited.FinalSkeleton, edited.BakedLods), saved);
                TestAssert.True(bytes.SequenceEqual(File.ReadAllBytes(source)), "Save As changed the input PCC.");
                using var reopenedReader = new MorphFacePackageReader();
                using var reopenedShell = CreateMainWindowViewModel(reopenedReader, playerBaselines: baselines);
                await OpenFace(reopenedShell, output, imported.ImportedFacePath);
                AssertEditable(reopenedShell, saved, $"{sex} reopened");

                // Caller-selected modes retain their original meaning; only automatic PCC mode changes.
                using var modeReader = new MorphFacePackageReader();
                var service = new MorphFacePreviewLoadService(new HeadPreviewSceneFactory(), new MorphTargetCatalog(),
                    MorphFaceProfileRegistry.CreateDefault(), modeReader, playerBaselines: baselines);
                var fixedBake = await service.LoadAsync(source, imported.ImportedFacePath, geometryMode: MorphFaceGeometryMode.FixedBake);
                TestAssert.True(!fixedBake.EditingSession.CanEditMorphFeatures, "Explicit FixedBake enabled morph controls.");
                var evaluated = await service.LoadAsync(source, imported.ImportedFacePath, geometryMode: MorphFaceGeometryMode.MorphEvaluated);
                TestAssert.True(!evaluated.EditingSession.CanEditMorphFeatures && evaluated.EditingSession.CanFixMorph,
                    "Explicit MorphEvaluated was automatically changed to RelativeBake.");
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    });

    private static void BlockedBaseline(bool changedRig) => RunWithDispatcher(async () =>
    {
        RequireSeeds(MorphFaceGame.LE1);
        using var generated = new QaSyntheticPlayerBaselines([MorphFaceGame.LE1], [StandalonePlayerSex.Female, StandalonePlayerSex.Male]);
        var baselines = generated.Catalog;
        var directory = NewDirectory();
        try
        {
            using var reader = new MorphFacePackageReader();
            var head = CreateHead(reader, StandalonePlayerSex.Female, baselines);
            if (changedRig)
                head = head with { MorphData = head.MorphData with { MorphFeatures = [], FinalSkeleton = [] } };
            else
            {
                var lods = head.MorphData.BakedLods.Select(l => l.ToArray()).ToArray();
                lods[0][0] += new Vector3(0, 0.1f, 0);
                head = head with { MorphData = head.MorphData with { BakedLods = lods } };
            }
            using var imported = new StandalonePlayerMorphImportService().ImportPlayerHead(MorphFaceGame.LE1, head, "UnsupportedBaseline");
            var source = Path.Combine(directory, "Unsupported.pcc");
            File.Copy(imported.Workspace.WorkingPath, source);
            if (changedRig)
            {
                using var package = MEPackageHandler.OpenMEPackage(source, forceLoadFromDisk: true);
                var face = package.FindExport(imported.ImportedFacePath, "BioMorphFace")
                    ?? throw new Exception("Imported face not found.");
                var mesh = face.GetProperty<ObjectProperty>("m_oBaseHead")?.ResolveToEntry(package) as ExportEntry
                    ?? throw new Exception("Imported face's base mesh is not a local export.");
                var binary = mesh.GetBinaryData<SkeletalMesh>();
                binary.RefSkeleton[1].Name = new NameReference("MFE_TestChangedBone");
                mesh.WriteBinary(binary);
                package.Save();
            }
            var bytes = File.ReadAllBytes(source);
            using var shell = CreateMainWindowViewModel(reader, playerBaselines: baselines);
            await OpenFace(shell, source, imported.ImportedFacePath);
            TestAssert.True(!shell.Editor!.CanEditMorphFeatures && shell.Editor.Features.All(f => !f.IsEditable),
                "An unproven shape or rig exposed morph sliders.");
            TestAssert.True(bytes.SequenceEqual(File.ReadAllBytes(source)), "Opening changed the input PCC.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    });

    private static HeadMorphPayload CreateHead(MorphFacePackageReader reader, StandalonePlayerSex sex, PlayerMorphBaselineCatalog baselines)
    {
        var seed = LoadSeed(reader, MorphFaceGame.LE1, sex);
        var profile = MorphFaceProfileRegistry.CreateDefault().Require(MorphFaceGame.LE1,
            seed.Document.Source.InstancedPath, seed.Document.BaseHeadReference?.InstancedPath);
        var baseline = RequireBaseline(baselines, profile).MorphData;
        return MorphFacePackageContextService.CreateDefaultHead(seed) with { MorphData = baseline };
    }

    private static async Task OpenFace(MorphFaceEditor.ViewModels.MainWindowViewModel shell, string source, string facePath)
    {
        await shell.OpenDroppedFileAsync(source);
        shell.SelectedFace = shell.Faces.Single(face => face.InstancedPath == facePath);
        await ((AsyncRelayCommand)shell.LoadSelectedFaceCommand).ExecuteAsync();
        TestAssert.True(shell.Editor is not null, shell.ErrorMessage ?? "Known baseline did not load.");
    }

    private static void AssertEditable(MorphFaceEditor.ViewModels.MainWindowViewModel shell, MorphFaceMorphData original, string label)
    {
        var editor = shell.Editor!;
        var draft = editor.CreateDraft();
        var expectedFeatures = original.MorphFeatures.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var actualFeatures = draft.MorphFeatures.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        PackageTestFixtures.AssertMorphEqual(original with { MorphFeatures = expectedFeatures },
            new(actualFeatures, draft.FinalSkeleton, draft.BakedLods));
        TestAssert.True(!editor.IsDirty && !shell.IsDirty && !editor.HasPendingRepair,
            $"{label} opened dirty or pending repair.");
        TestAssert.True(editor.CanEditMorphFeatures && editor.Features.Any(f => f.IsEditable), $"{label} baseline sliders blocked.");
        TestAssert.True(!shell.FixMorphCommand.CanExecute(null), $"{label} baseline offers Fix Morph.");
    }

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"MFE-KnownBaselineUi-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
