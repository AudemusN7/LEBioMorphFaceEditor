using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.QA;

internal static partial class QaCases
{
    private static async Task PreparePlayer(QaContext c)
    {
        using var phase = c.UseStagePhase("Player fixture preparation");
        var stock = c.Source();
        c.Current.Evidence["stockPlayerSource"] = stock;
        var preview = await c.Load(stock.PackagePath, stock.FacePath, MorphFaceGeometryMode.RelativeBake);
        using var scope = await c.Editor(preview, stock.PackagePath, player: true);
        var editor = scope.Editor;
        var before = editor.CreateDraft();
        var materials = MaterialSignature(editor.Material.CaptureInterchangeData());
        // Fixture preparation is deliberately outside randomisation benchmark samples.
        await editor.RandomiseAsync();
        var seeded = editor.CreateDraft();
        QaContext.Require(!MorphFaceEditorStateComparer.Equals(before, seeded) &&
            materials != MaterialSignature(editor.Material.CaptureInterchangeData()) &&
            (!before.MorphFeatures.SequenceEqual(seeded.MorphFeatures) ||
             before.BakedLods.Zip(seeded.BakedLods).Any(pair => !pair.First.SequenceEqual(pair.Second))),
            "Player fixture seed did not change both morph geometry and material values.");
        var saved = await c.Save(seeded, stock.PackagePath, "seeded-player.pcc");
        c.PreparedPlayer = MorphFaceReferenceInspector.Inspect(saved.PackagePath)
            .Single(value => value.FacePath == saved.FaceInstancedPath);
        c.TrackInput(saved.PackagePath);
        c.Current.Evidence["playerFixtureSeed"] = new
        {
            RunSeed = c.Seed, MorphStrength = 100, MaterialStrength = 100,
            PackagePath = saved.PackagePath, FacePath = saved.FaceInstancedPath,
            GeometryChanged = true, MaterialsChanged = true, RandomisationBenchmarked = false
        };
    }
}
