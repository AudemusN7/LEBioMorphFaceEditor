using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Services;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;
using static MorphFaceEditor.Tests.UiTestFixtures;

namespace MorphFaceEditor.Tests;

internal static class FemaleTurianTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
        [new("female Turian embedded targets contain valid three-LOD channels", EmbeddedTargets)];
    internal static IReadOnlyList<TestCase> Ui { get; } =
    [
        new("new female Turian NPC edits, saves, converts and exports with installed donors", NewNpcRoundTrip),
        new("new female Turian NPC requires an installed donor", MissingDonor)
    ];

    private static readonly MorphFaceGame[] Games = [MorphFaceGame.LE1, MorphFaceGame.LE2, MorphFaceGame.LE3];
    private const string MeshPath = "BIOG_TUF_HED_PROMorph_R.PROBase.TUF_HED_PROBase_MDL";

    private static void EmbeddedTargets()
    {
        TestAssert.True(typeof(MorphFacePackageReader).Assembly.GetManifestResourceNames().All(name =>
            !name.EndsWith(".pcc.br", StringComparison.OrdinalIgnoreCase)), "A source PCC was embedded in MFE.");
        var profiles = MorphFaceProfileRegistry.CreateDefault();
        var catalog = new MorphTargetCatalog();
        foreach (var game in Games)
        {
            var profile = profiles.Require(game, "Female Turian.Test", MeshPath);
            var targets = catalog.Load(profile);
            TestAssert.True(!profile.IgnoresAuthoredGeometry, $"{game} ignored female Turian geometry.");
            TestAssert.Equal(34, targets.Count);
            TestAssert.True(targets.Select(target => target.Source.InstancedPath.Split('.').Last())
                .ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(MorphFaceProfileFeatures.FemaleTurianTargetNames),
                $"{game} has incorrect female Turian target names.");
            foreach (var name in new[] { "headSpikes_Long", "headSpikes_Short", "neck_Thin", "eyes_Large" })
                TestAssert.True(!MorphFaceProfileFeatures.IsControlEligible("female-turian", game, name),
                    $"{game} exposed unsupported {name}.");
            foreach (var target in targets)
            {
                TestAssert.Equal(0, target.BoneOffsets.Count);
                TestAssert.Equal(3, target.Lods.Count);
                for (var i = 0; i < 3; i++)
                {
                    var lod = target.Lods[i];
                    TestAssert.Equal(i, lod.LodIndex);
                    TestAssert.True(lod.BaseMeshVertexCount > 0 && lod.Vertices.Count == lod.Vertices.Select(v => v.SourceIndex).Distinct().Count() &&
                        lod.Vertices.All(v => v.SourceIndex >= 0 && v.SourceIndex < lod.BaseMeshVertexCount &&
                            float.IsFinite(v.PositionDelta.X) && float.IsFinite(v.PositionDelta.Y) && float.IsFinite(v.PositionDelta.Z) &&
                            float.IsFinite(v.NormalDelta.X) && float.IsFinite(v.NormalDelta.Y) && float.IsFinite(v.NormalDelta.Z)),
                        $"{game} {target.Source.InstancedPath} LOD {i} has invalid target data.");
                }
            }
        }
    }

    private static void NewNpcRoundTrip() => WpfTestDispatcher.Invoke(() => RunWithDispatcher(async () =>
    {
        using var installed = new TufInstalledDonorFixture();
        var directory = Path.Combine(Path.GetTempPath(), $"MFE-TUF-roundtrip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var conversion = new MorphFaceConversionService(MorphFaceProfileRegistry.CreateDefault(), new MorphTargetCatalog(),
            new MorphFacePackageContextService(), installed.Catalog);
        try
        {
            foreach (var game in Games)
            {
                var donor = (await installed.Catalog.ReadAsync(game)).MorphFaceTemplates.Single();
                var fingerprint = PackageFingerprint.Capture(donor.PackagePath);
                var ron = Path.Combine(directory, $"{game}.ron");
                var output = Path.Combine(directory, $"{game}.pcc");
                var dialogs = new StubEditorDialogs
                {
                    NewNpcMorphChoiceResult = new(game, "TUF"),
                    StandaloneNameChoiceResult = "MFE_TUF_New",
                    RonExportPath = ron
                };
                using var reader = new MorphFacePackageReader();
                using var shell = CreateMainWindowViewModel(reader, dialogs, installed.Catalog);
                await ((AsyncRelayCommand)shell.NewNpcMorphCommand).ExecuteAsync();
                var editor = shell.Editor ?? throw new Exception(shell.ErrorMessage ?? $"{game} New NPC failed.");
                TestAssert.True(shell.HasPreview && !shell.HasError && editor.CanEditMorphFeatures && editor.Features.Count == 34,
                    $"{game} did not open an editable female Turian.");
                await CheckChooserAsync(game, installed.Catalog, expectedTuf: true);
                var before = editor.CreateDraft();
                editor.Features.Single(feature => feature.Name == "brow_Up").Value = 0.5f;
                var draft = editor.CreateDraft();
                TestAssert.Equal(3, draft.BakedLods.Count);
                for (var i = 0; i < 3; i++)
                    TestAssert.True(!draft.BakedLods[i].SequenceEqual(before.BakedLods[i]), $"{game} slider did not change LOD {i}.");

                var saved = new MorphFacePackageWriter().SaveMorphToPackage(draft, draft.Source.PackagePath, output, true);
                using var reopenedReader = new MorphFacePackageReader();
                var reopened = reopenedReader.Load(output, saved.FaceInstancedPath, decodeTextures: false).Document;
                TestAssert.Near(0.5f, reopened.GetFeatureOffset("brow_Up"), 0);
                TestAssert.Equal(3, reopened.BakedLods.Count);
                for (var i = 0; i < 3; i++)
                    TestAssert.True(draft.BakedLods[i].SequenceEqual(reopened.BakedLods[i]), $"{game} save changed LOD {i}.");

                foreach (var target in Games.Where(value => value != game))
                {
                    var converted = conversion.Convert(new MorphFaceConversionRequest(output, saved.FaceInstancedPath,
                        target, Path.Combine(directory, $"{game}-to-{target}.pcc"), true, null));
                    using var convertedReader = new MorphFacePackageReader();
                    var port = convertedReader.Load(converted.SaveResult.PackagePath,
                        converted.SaveResult.FaceInstancedPath, decodeTextures: false).Document;
                    TestAssert.Near(0.5f, port.GetFeatureOffset("brow_Up"), 0);
                    TestAssert.Equal(3, port.BakedLods.Count);
                }

                await ((AsyncRelayCommand)shell.ExportMorphRonCommand).ExecuteAsync();
                TestAssert.True(!shell.HasError && File.Exists(ron), shell.ErrorMessage ?? $"{game} full RON export failed.");
                var full = TseHeadMorphRon.ReadDocument(ron);
                TestAssert.Equal("TUF", full.Provenance!.Archetype);
                TestAssert.Equal(3, full.Head.MorphData.BakedLods.Count);
                for (var i = 0; i < 3; i++)
                    TestAssert.True(draft.BakedLods[i].SequenceEqual(full.Head.MorphData.BakedLods[i]), $"{game} full RON changed LOD {i}.");
                File.Delete(ron);
                await ((AsyncRelayCommand)shell.ExportMorphFeaturesRonCommand).ExecuteAsync();
                TestAssert.True(!shell.HasError && TseHeadMorphRon.IsMorphFeaturesOnly(ron), shell.ErrorMessage ?? $"{game} slider RON failed.");
                var features = TseHeadMorphRon.ReadDocument(ron);
                TestAssert.Equal("TUF", features.Provenance!.Archetype);
                TestAssert.True(draft.MorphFeatures.SequenceEqual(features.Head.MorphData.MorphFeatures), $"{game} slider RON changed features.");
                TestAssert.Equal(fingerprint, PackageFingerprint.Capture(donor.PackagePath));
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }), TimeSpan.FromMinutes(2));

    private static void MissingDonor() => WpfTestDispatcher.Invoke(() => RunWithDispatcher(async () =>
    {
        using var installed = new TufInstalledDonorFixture(includeDonors: false);
        foreach (var game in Games)
        {
            using var reader = new MorphFacePackageReader();
            var dialogs = new StubEditorDialogs { NewNpcMorphChoiceResult = new(game, "TUF"), StandaloneNameChoiceResult = "MissingTuf" };
            using var shell = CreateMainWindowViewModel(reader, dialogs, installed.Catalog);
            await ((AsyncRelayCommand)shell.NewNpcMorphCommand).ExecuteAsync();
            TestAssert.True(shell.HasError && shell.Editor is null && !shell.HasPreview && !shell.IsDirty,
                $"{game} created a workspace without an installed donor.");
            TestAssert.True(shell.ErrorMessage!.Contains(Loc.Format(
                "loc_Services_RonNpcDonorResolver_NoInstalledNativeNpcFace", game, "TUF"), StringComparison.Ordinal), shell.ErrorMessage);
            await CheckChooserAsync(game, installed.Catalog, expectedTuf: false);
        }
    }));

    private static async Task CheckChooserAsync(MorphFaceGame game,
        MorphFaceEditor.LegendaryExplorer.TextureRegistry.TextureCatalogService catalog, bool expectedTuf)
    {
        var window = new MorphFaceEditor.Views.StandaloneImportGameWindow(newNpcMode: true, textureCatalog: catalog);
        var selector = (MorphFaceEditor.Controls.GameSelector)window.FindName("TargetGameSelector");
        selector.SelectedItem = game;
        await (Task)typeof(MorphFaceEditor.Views.StandaloneImportGameWindow)
            .GetMethod("RefreshArchetypesAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(window, null)!;
        var box = (System.Windows.Controls.ComboBox)window.FindName("ArchetypeBox");
        TestAssert.Equal(expectedTuf, box.Items.Cast<RonNpcArchetypeOption>().Any(option => option.Key == "TUF"));
        window.Close();
    }
}
