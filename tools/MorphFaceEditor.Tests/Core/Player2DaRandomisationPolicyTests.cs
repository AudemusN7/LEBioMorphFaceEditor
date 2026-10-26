using System.Numerics;
using MorphFaceEditor.Core.Randomisation;

namespace MorphFaceEditor.Tests;

public static class Player2DaRandomisationPolicyTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("Player 2DA conversion preserves alpha and only writes allowed parameters", SrgbAndAllowedParameters),
        new("Player hair and facial hair palettes are not narrowed by skin ranges", FullHairAndFacialHairPalettes),
    ];

    private static void SrgbAndAllowedParameters()
    {
        var request = new Player2DaRandomisationRequest(
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
        var request = new Player2DaRandomisationRequest(
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
        var inactive = Player2DaRandomisationPolicy.Roll(
            request with { HasBrowTexture = false, HasBeardTexture = false }, new FixedRandom(21));
        TestAssert.True(!inactive.SelectedRows.ContainsKey(Player2DaPalette.FacialHairColour),
            "Inactive addition slots should preserve their authored colours.");
    }

    private static float ToLinear(float value) => value <= 0.04045f
        ? value / 12.92f
        : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);

    private sealed class FixedRandom(int value) : Random
    {
        public override int Next(int maxValue) => maxValue <= 1 ? 0 : Math.Clamp(value, 0, maxValue - 1);
    }
}
