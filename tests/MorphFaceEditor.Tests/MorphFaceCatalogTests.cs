using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.Tests;

public static class MorphFaceCatalogTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("LE1 EntryMenu Player staging hides only Broke Asari and Krogan", Le1EntryMenuPlayerStagingHidesOnlyKnownFaces),
        new("LE1 staging projection does not affect default or other-game catalogues", Le1StagingProjectionIsRouteAndGameScoped),
        new("LE1 EntryMenu projection policy is limited to detached Player staging", Le1EntryMenuProjectionPolicyIsNarrow)
    ];

    private static void Le1EntryMenuPlayerStagingHidesOnlyKnownFaces()
    {
        var faces = new[]
        {
            Reference(MorphFaceGame.LE1, "BIOG_MORPH_FACE.Broke"),
            Reference(MorphFaceGame.LE1, "BIOG_MORPH_FACE.Asari"),
            Reference(MorphFaceGame.LE1, "BIOG_MORPH_FACE.Krogan"),
            Reference(MorphFaceGame.LE1, "BIOG_MORPH_FACE.Player_Base_Male"),
            Reference(MorphFaceGame.LE1, "BIOG_MORPH_FACE.Player_Base_Female")
        };

        var visible = faces
            .Where(face => !MorphFaceCatalogService.IsHiddenForProjection(
                face, MorphFaceCatalogProjection.Le1EntryMenuPlayerStaging))
            .Select(face => face.FacePath)
            .ToArray();

        TestAssert.Equal(2, visible.Length);
        TestAssert.True(visible.SequenceEqual(
                ["BIOG_MORPH_FACE.Player_Base_Male", "BIOG_MORPH_FACE.Player_Base_Female"]),
            "The LE1 Player staging projection hid a face other than the three known EntryMenu exports.");
    }

    private static void Le1StagingProjectionIsRouteAndGameScoped()
    {
        var hiddenNames = new[] { "Broke", "Asari", "Krogan" };
        foreach (var name in hiddenNames)
        {
            var le1 = Reference(MorphFaceGame.LE1, $"BIOG_MORPH_FACE.{name}");
            var le2 = Reference(MorphFaceGame.LE2, le1.FacePath);

            TestAssert.True(
                MorphFaceCatalogService.IsHiddenForProjection(
                    le1, MorphFaceCatalogProjection.Le1EntryMenuPlayerStaging),
                $"LE1 {le1.FacePath} was not hidden by the explicit staging projection.");
            TestAssert.True(
                !MorphFaceCatalogService.IsHiddenForProjection(
                    le2, MorphFaceCatalogProjection.Le1EntryMenuPlayerStaging),
                $"The LE1 staging projection leaked to another game for {le2.FacePath}.");
            TestAssert.True(
                !MorphFaceCatalogService.IsHiddenForProjection(
                    le1, MorphFaceCatalogProjection.Default),
                $"Default package face discovery hid {le1.FacePath}.");
        }
    }

    private static void Le1EntryMenuProjectionPolicyIsNarrow()
    {
        TestAssert.Equal(
            MorphFaceCatalogProjection.Le1EntryMenuPlayerStaging,
            MorphFaceCatalogProjectionPolicy.ForPlayerWorkspace(
                MorphFaceGame.LE1, @"D:\Game\ME1\BioGame\CookedPCConsole\EntryMenu.pcc", false));
        TestAssert.Equal(
            MorphFaceCatalogProjection.Default,
            MorphFaceCatalogProjectionPolicy.ForPlayerWorkspace(
                MorphFaceGame.LE1, @"D:\Game\ME1\BioGame\CookedPCConsole\BioP_Char.pcc", false));
        TestAssert.Equal(
            MorphFaceCatalogProjection.Default,
            MorphFaceCatalogProjectionPolicy.ForPlayerWorkspace(
                MorphFaceGame.LE2, @"D:\Game\ME2\BioGame\CookedPCConsole\EntryMenu.pcc", false));
        TestAssert.Equal(
            MorphFaceCatalogProjection.Default,
            MorphFaceCatalogProjectionPolicy.ForPlayerWorkspace(
                MorphFaceGame.LE1, @"D:\Game\ME1\BioGame\CookedPCConsole\EntryMenu.pcc", true));
    }

    private static MorphFaceMeshReferences Reference(MorphFaceGame game, string path) =>
        new("EntryMenu.pcc", game, 1, path, null, null, []);
}
