using System.Numerics;
using MorphFaceEditor.Core.Randomisation;

namespace MorphFaceEditor.Tests;

public static class Player2DaRandomisationPolicyTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("Player 2DA tables retain reviewed row counts and full hair palettes", EmbeddedTablesAndLinkedRows),
        new("Player 2DA conversion preserves alpha and only writes allowed parameters", SrgbAndAllowedParameters),
        new("Player hair and facial hair palettes are not narrowed by skin ranges", FullHairAndFacialHairPalettes),
        new("Player addition colours keep paired values and require active textures", ActiveAdditionSlotsUseFullPalette),
        new("HMF eye makeup uses row weights and keeps lash metadata with the row", EyeMakeupKeepsWeightedLashRow)
    ];

    private static void EmbeddedTablesAndLinkedRows()
    {
        TestAssert.Equal(28, Player2DaRandomisationPolicy.GetRowCount(Player2DaPalette.SkinTone));
        TestAssert.Equal(22, Player2DaRandomisationPolicy.GetRowCount(Player2DaPalette.HairColour));
        TestAssert.Equal(20, Player2DaRandomisationPolicy.GetRowCount(Player2DaPalette.FacialHairColour));
        TestAssert.Equal(17, Player2DaRandomisationPolicy.GetRowCount(Player2DaPalette.IrisColour));
        TestAssert.Equal(31, Player2DaRandomisationPolicy.GetRowCount(Player2DaPalette.Blush));
        TestAssert.Equal(34, Player2DaRandomisationPolicy.GetRowCount(Player2DaPalette.EyeMakeup));
        TestAssert.Equal(32, Player2DaRandomisationPolicy.GetRowCount(Player2DaPalette.Lips));

        var request = new Player2DaRandomisationRequest(Player2DaSex.Male,
            new HashSet<Player2DaPalette> { Player2DaPalette.SkinTone, Player2DaPalette.HairColour, Player2DaPalette.FacialHairColour, Player2DaPalette.IrisColour },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "SkinTone", "HED_Hair_Colour_Vector", "HED_Addn_Colour_Vector", "EYE_Iris_Colour_Vector"
            },
            new Dictionary<string, Player2DaValue>());

        var result = Player2DaRandomisationPolicy.Roll(request, new FixedRandom(0));

        TestAssert.Equal(1, result.SelectedRows[Player2DaPalette.SkinTone]);
        TestAssert.Equal(1, result.SelectedRows[Player2DaPalette.HairColour]);
        TestAssert.Equal(1, result.SelectedRows[Player2DaPalette.FacialHairColour]);
        TestAssert.True(result.Values.ContainsKey("SkinTone") && result.Values.ContainsKey("HED_Hair_Colour_Vector"),
            "Skin and linked hair rows should be returned for integration.");

        var lockedSkin = new Player2DaRandomisationRequest(Player2DaSex.Male,
            new HashSet<Player2DaPalette> { Player2DaPalette.HairColour },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "HED_Hair_Colour_Vector" },
            new Dictionary<string, Player2DaValue>
            {
                ["SkinTone"] = Player2DaValue.FromVector(new Vector4(ToLinear(53f / 255f), ToLinear(43f / 255f), ToLinear(33f / 255f), 1))
            });
        var linkedToLockedSkin = Player2DaRandomisationPolicy.Roll(lockedSkin, new FixedRandom(0));
        TestAssert.Equal(1, linkedToLockedSkin.SelectedRows[Player2DaPalette.HairColour]);
    }

    private static void SrgbAndAllowedParameters()
    {
        var request = new Player2DaRandomisationRequest(Player2DaSex.Female,
            new HashSet<Player2DaPalette> { Player2DaPalette.SkinTone },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SkinTone" },
            new Dictionary<string, Player2DaValue>
            {
                ["SkinTone"] = Player2DaValue.FromVector(new Vector4(0, 0, 0, 0.37f))
            });

        var result = Player2DaRandomisationPolicy.Roll(request, new FixedRandom(0));
        var skin = result.Values["SkinTone"].Vector;
        var expected = ToLinear(53f / 255f);
        TestAssert.Near(expected, skin.X, 0.000001f);
        TestAssert.Near(0.37f, skin.W, 0.000001f);
        TestAssert.True(!result.Values.ContainsKey("HED_Scar_Vector"),
            "A supported name omitted from AllowedParameters must be treated as locked or out of scope.");
        TestAssert.True(!result.Values.ContainsKey("EYE_White_Colour_Vector"),
            "A missing material parameter must remain untouched.");
    }

    private static void FullHairAndFacialHairPalettes()
    {
        var request = new Player2DaRandomisationRequest(Player2DaSex.Male,
            new HashSet<Player2DaPalette> { Player2DaPalette.SkinTone, Player2DaPalette.HairColour, Player2DaPalette.FacialHairColour },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SkinTone", "HED_Hair_Colour_Vector", "HED_Addn_Colour_Vector", "blonde" },
            new Dictionary<string, Player2DaValue>
            {
                ["HED_Addn_Colour_Vector"] = Player2DaValue.FromVector(new Vector4(0, 0, 0, 0.6f)),
                ["blonde"] = Player2DaValue.FromVector(new Vector4(0, 0, 0, 0.4f))
            }, HasBrowTexture: true, HasBeardTexture: true);

        // FixedRandom selects the last available row. SkinTone1 historically reduced these to row 6.
        var result = Player2DaRandomisationPolicy.Roll(request, new FixedRandom(21));
        TestAssert.Equal(22, result.SelectedRows[Player2DaPalette.HairColour]);
        TestAssert.Equal(20, result.SelectedRows[Player2DaPalette.FacialHairColour]);
        TestAssert.True(result.Values.ContainsKey("HED_Addn_Colour_Vector") && result.Values.ContainsKey("blonde"),
            "Both addition colour channels must come from the selected facial-hair row.");
        TestAssert.True(result.Values["HED_Addn_Colour_Vector"].Vector != result.Values["blonde"].Vector,
            "The separate facial-hair and brow colour columns were collapsed.");
    }

    private static void EyeMakeupKeepsWeightedLashRow()
    {
        var request = new Player2DaRandomisationRequest(Player2DaSex.Female,
            new HashSet<Player2DaPalette> { Player2DaPalette.EyeMakeup },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "HED_EyeShadow_Tint_Scalar", "HED_EyeShadow_Tint_Vector", "HED_Brow_Tint_Scalar",
                "HED_Brow_Tint_Vector", "HED_Lash_Diff"
            },
            new Dictionary<string, Player2DaValue>
            {
                ["HED_EyeShadow_Tint_Vector"] = Player2DaValue.FromVector(new Vector4(0, 0, 0, 0.42f))
            });

        // The first 50 weight points are the None row; point 50 starts Nude_Light_Low.
        var result = Player2DaRandomisationPolicy.Roll(request, new FixedRandom(50));
        TestAssert.Equal(2, result.SelectedRows[Player2DaPalette.EyeMakeup]);
        TestAssert.Equal("HMF_HED_PROLash_Opac_M01", result.Values["HED_Lash_Diff"].Texture);
        TestAssert.Near(0.42f, result.Values["HED_EyeShadow_Tint_Vector"].Vector.W, 0.000001f);
        TestAssert.True(result.Values.ContainsKey("HED_Brow_Tint_Vector"),
            "Eye row brow tint must stay correlated with its eye makeup preset.");
    }

    private static void ActiveAdditionSlotsUseFullPalette()
    {
        var palettes = new HashSet<Player2DaPalette> { Player2DaPalette.HairColour, Player2DaPalette.FacialHairColour };
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "HED_Hair_Colour_Vector", "HED_Addn_Colour_Vector"
        };
        var current = new Dictionary<string, Player2DaValue>();
        var baseRequest = new Player2DaRandomisationRequest(Player2DaSex.Male, palettes, allowed, current, CurrentSkinRow: 14);

        var browOnly = Player2DaRandomisationPolicy.Roll(
            baseRequest with { HasBrowTexture = true, HasBeardTexture = false }, new FixedRandom(0));
        TestAssert.Equal(1, browOnly.SelectedRows[Player2DaPalette.FacialHairColour]);

        var beardOnly = Player2DaRandomisationPolicy.Roll(
            baseRequest with { HasBrowTexture = false, HasBeardTexture = true }, new FixedRandom(19));
        TestAssert.Equal(20, beardOnly.SelectedRows[Player2DaPalette.FacialHairColour]);

        var both = Player2DaRandomisationPolicy.Roll(
            baseRequest with { HasBrowTexture = false, HasBeardTexture = false }, new FixedRandom(1));
        TestAssert.True(!both.SelectedRows.ContainsKey(Player2DaPalette.FacialHairColour),
            "Absent Player texture slots must retain their current colour values.");

        var neither = Player2DaRandomisationPolicy.Roll(
            baseRequest with { HasBrowTexture = false, HasBeardTexture = false }, new FixedRandom(0));
        TestAssert.True(!neither.SelectedRows.ContainsKey(Player2DaPalette.FacialHairColour),
            "No addition-color tuple should be selected if neither brow nor beard texture is active.");
    }

    private static float ToLinear(float value) => value <= 0.04045f
        ? value / 12.92f
        : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);

    private sealed class FixedRandom(int value) : Random
    {
        public override int Next(int maxValue) => maxValue <= 1 ? 0 : Math.Clamp(value, 0, maxValue - 1);
    }
}
