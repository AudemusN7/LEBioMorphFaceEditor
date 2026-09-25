using System.Globalization;
using System.Numerics;

namespace MorphFaceEditor.Core.Randomisation;

public enum Player2DaSex { Male, Female }

public enum Player2DaPalette
{
    SkinTone,
    HairColour,
    FacialHairColour,
    IrisColour,
    Blush,
    EyeMakeup,
    Lips
}

public enum Player2DaValueKind { Vector, Scalar, Texture }

public sealed record Player2DaValue(Player2DaValueKind Kind, Vector4 Vector, float Scalar, string? Texture)
{
    public static Player2DaValue FromVector(Vector4 value) => new(Player2DaValueKind.Vector, value, 0, null);
    public static Player2DaValue FromScalar(float value) => new(Player2DaValueKind.Scalar, default, value, null);
    public static Player2DaValue FromTexture(string value) => new(Player2DaValueKind.Texture, default, 0, value);
}

public sealed record Player2DaRandomisationRequest(
    Player2DaSex Sex,
    IReadOnlySet<Player2DaPalette> Palettes,
    IReadOnlySet<string> AllowedParameters,
    IReadOnlyDictionary<string, Player2DaValue> CurrentValues,
    int? CurrentSkinRow = null,
    bool HasBrowTexture = true,
    bool HasBeardTexture = true);

public sealed record Player2DaRandomisationResult(
    IReadOnlyDictionary<string, Player2DaValue> Values,
    IReadOnlyDictionary<Player2DaPalette, int> SelectedRows);

/// <summary>
/// Rolls reviewed character-creator 2DA presets. Pass only parameters that are both in scope and unlocked.
/// Row ranges use inclusive, 1-based workbook row keys; -1/-1 means unrestricted and any empty range
/// leaves that channel untouched. Vector alpha is copied from the current material value.
/// </summary>
public static class Player2DaRandomisationPolicy
{
    private sealed record Row(int Key, int Weight, string[] Fields, IReadOnlyDictionary<string, (double Min, double Max)> Ranges);

    private const string SkinData = """
1|53,43,33|192,184,169|150,151,148|85,73,57|237,189,125|6,6|6,6|10,12
2|64,43,38|192,184,169|150,151,148|97,72,65|237,189,125|6,6|6,6|10,12
3|82,37,34|192,184,169|150,151,148|113,64,60|237,189,125|6,6|6,6|10,12
4|95,52,33|192,184,169|150,151,148|121,84,57|237,189,125|6,6|6,6|10,12
5|110,55,35|192,184,169|150,151,148|134,86,63|237,189,125|6,6|6,6|10,12
6|137,72,52|206,190,172|160,161,158|160,105,84|237,189,125|6,6|6,6|10,12
7|155,71,47|206,190,172|160,161,158|179,104,75|237,189,125|6,6|6,6|10,12
8|161,74,65|206,190,172|160,161,158|182,108,97|237,189,125|6,6|6,6|10,12
9|122,79,59|192,184,169|150,151,148|136,92,69|237,189,125|6,6|6,6|10,12
10|126,82,59|192,184,169|150,151,148|136,92,69|237,189,125|6,6|6,6|10,12
11|124,88,72|206,190,172|160,161,158|147,117,106|237,189,125|6,6|6,6|10,12
12|149,88,67|206,190,172|160,161,158|173,117,100|237,189,125|6,6|6,6|10,12
13|163,104,73|206,190,172|160,161,158|173,114,83|237,189,125|6,6|6,6|10,12
14|173,97,61|220,209,194|180,181,178|198,124,95|237,189,125|5,6|5,6|-1,-1
15|169,109,85|220,209,194|180,181,178|197,132,114|237,189,125|5,6|5,6|-1,-1
16|187,121,92|220,209,194|180,181,178|197,132,114|237,189,125|5,6|5,6|-1,-1
17|192,128,95|220,209,194|180,181,178|187,138,110|237,189,125|5,6|5,6|-1,-1
18|174,130,91|220,209,194|180,181,178|187,138,110|237,189,125|5,6|5,6|-1,-1
19|191,135,100|220,209,194|180,181,178|214,157,128|237,189,125|5,6|5,6|-1,-1
20|213,148,118|229,222,211|200,200,198|232,171,142|237,189,125|3,6|3,6|-1,-1
21|225,158,125|229,222,211|200,200,198|233,155,119|237,189,125|3,6|3,6|-1,-1
22|239,177,150|243,238,230|200,200,198|253,186,162|237,189,125|1,6|1,6|-1,-1
23|252,189,162|250,243,232|200,200,198|253,186,162|237,189,125|1,6|1,6|-1,-1
24|252,203,175|250,243,232|223,210,200|253,186,162|237,189,125|1,6|1,6|-1,-1
25|218,166,127|229,222,211|200,200,198|234,189,150|237,189,125|3,6|3,6|-1,-1
26|221,175,149|229,222,211|200,200,198|236,199,172|237,189,125|3,6|3,6|-1,-1
27|222,181,151|243,238,230|200,200,198|238,205,174|237,189,125|1,6|1,6|-1,-1
28|221,193,179|243,238,230|200,200,198|238,216,202|237,189,125|1,6|1,6|-1,-1
""";

    // The reviewed male/female 2DAs have identical hair colour rows. Keep the common table once.
    private const string HairData = """
1|255,240,190|0|3|50|15|3|1|4,4|3
2|255,225,175|0|3|50|15|3|1|4,4|3
3|250,211,154|0|3|200|0|2.75|1.5|0,0|1.5
4|202,172,118|0|3|50|15|3|1|1,0|1.5
5|159,118,80|0|3|50|15|3|1|1,1|1.5
6|95,57,22|0|3|50|15|3|1|4,4|1.5
7|87,63,43|0|3|200|15|3|1|1,4|1.5
8|127,83,62|0|3|200|15|3|1|1,3|1
9|96,52,43|0|3|200|15|1|1|0.25,2|1.5
10|43,35,32|0|3|200|15|5|0.85|1,5|1
11|22,19,19|0|3|200|15|5|0.85|1,6|1
12|68,68,68|0|3|150|15|3|1|4,4|1.5
13|100,100,100|0|3|150|15|3|1|4,4|1.5
14|140,140,140|0|3|150|15|3|1|4,4|1.5
15|217,217,217|0|3|50|15|3|1|4,4|1.5
16|219,226,248|0|3|50|15|3|1|4,4|1.5
17|150,52,43|0|3|50|15|3|1|4,4|1.5
18|183,62,49|0|3|50|15|3|1|4,4|1.5
19|230,73,56|0|3|50|15|3|1|4,4|1.5
20|250,80,126|0|3|50|15|3|1|4,4|1.5
21|173,56,177|0|3|50|15|3|1|4,4|1.5
22|96,38,98|0|3|50|15|3|1|4,4|1.5
""";

    private const string FacialHairData = """
1|191,180,143|191,180,143|1|1
2|191,169,131|191,169,131|1|1
3|157,131,83|199,177,139|1|1
4|145,93,63|192,153,112|1|1
5|48,29,11|48,29,11|1|1.35
6|60,35,22|57,40,32|1|1.15
7|110,60,46|89,32,12|1|1.35
8|127,83,62|110,60,46|1|1.35
9|0,0,0|28,19,16|1|1.5
10|15,15,15|15,15,15|1|1.15
11|50,50,50|50,50,50|1|1.5
12|70,70,70|70,70,70|1|1.5
13|109,109,109|109,109,109|1|1.5
14|110,113,124|110,113,124|1|1.5
15|75,26,22|75,26,22|1|1.5
16|92,31,25|92,31,25|1|1.5
17|115,37,28|115,37,28|1|1.5
18|125,40,63|125,40,63|1|1.5
19|87,28,89|87,28,89|1|1.5
20|48,19,49|48,19,49|1|1.5
""";

    private const string IrisData = """
1|172,191,176
2|110,147,107
3|85,158,94
4|166,206,114
5|166,163,184
6|110,142,184
7|90,90,107
8|4,48,195
9|199,132,97
10|188,103,67
11|141,85,55
12|189,100,84
13|157,97,78
14|30,30,30
15|175,17,17
16|225,220,220
17|189,177,255
""";

    private const string BlushData = """
1|0|0,0,0|50
2|0.25|217,166,123|10
3|1|217,166,123|10
4|1.31|217,166,123|10
5|0.25|228,176,175|10
6|1|228,176,175|10
7|1.31|228,176,175|10
8|0.3|245,168,156|10
9|1.2|245,168,156|10
10|1.58|245,168,156|10
11|0.23|219,88,88|10
12|0.75|219,88,88|10
13|0.93|219,88,88|10
14|0.25|187,94,79|10
15|0.9|187,94,79|10
16|1.05|187,94,79|10
17|0.28|166,75,44|10
18|1|166,75,44|10
19|1.14|166,75,44|10
20|0.33|183,115,112|10
21|1|183,115,112|10
22|1.14|183,115,112|10
23|0.33|212,104,83|10
24|1|212,104,83|10
25|1.14|212,104,83|10
26|0.33|206,139,130|10
27|1|206,139,130|10
28|1.14|206,139,130|10
29|0.33|205,158,157|10
30|1|205,158,157|10
31|1.14|205,158,157|10
""";

    // Makeup tables use the exact 2DA per-row weights. The Eye table keeps the lash object metadata attached to each row.
    private const string EyeData = """
1|0|0,0,0|0|0,0,0|HMF_HED_PROLash_Opac_M01|50
2|0.5|64,54,53|0.5|150,120,112|HMF_HED_PROLash_Opac_M01|10
3|1|64,54,53|1|150,120,112|HMF_HED_PROLash_Opac_M01|10
4|1|64,54,53|2|150,120,112|HMF_HED_PROLash_Opac_M01|10
5|0.5|88,69,62|0.4|184,143,137|HMF_HED_PROLash_Opac_M01|10
6|1|88,69,62|0.8|184,143,137|HMF_HED_PROLash_Opac_M01|10
7|1|88,69,62|1.6|184,143,137|HMF_HED_PROLash_Opac_M01|10
8|0.5|108,67,28|0.1|199,145,91|HMF_HED_PROLash_Opac_M01|10
9|1|108,67,28|0.2|199,145,91|HMF_HED_PROLash_Opac_M01|10
10|1|108,67,28|0.4|199,145,91|HMF_HED_PROLash_Opac_M01|10
11|0.5|54,23,22|0.45|150,67,50|HMF_HED_PROLash_Opac_M02|30
12|1|54,23,22|0.9|150,67,50|HMF_HED_PROLash_Opac_M02|30
13|1|54,23,22|1.8|150,67,50|HMF_HED_PROLash_Opac_M02|30
14|0.5|43,22,33|0.225|30,28,30|HMF_HED_PROLash_Opac_M02|30
15|1|43,22,33|0.55|30,28,30|HMF_HED_PROLash_Opac_M02|30
16|1|43,22,33|1.1|30,28,30|HMF_HED_PROLash_Opac_M02|30
17|0.225|20,20,30|0|0,0,0|HMF_HED_PROLash_Opac_M02|70
18|0.55|20,20,30|0|0,0,0|HMF_HED_PROLash_Opac_M02|70
19|1|20,20,30|0|0,0,0|HMF_HED_PROLash_Opac_M02|70
20|0.4|60,50,76|0.175|192,85,130|HMF_HED_PROLash_Opac_M02|30
21|0.8|60,50,76|0.35|192,85,130|HMF_HED_PROLash_Opac_M02|30
22|0.8|60,50,76|0.7|192,85,130|HMF_HED_PROLash_Opac_M02|30
23|0.4|80,47,58|0.25|205,65,112|HMF_HED_PROLash_Opac_M02|30
24|0.8|80,47,58|0.5|205,65,112|HMF_HED_PROLash_Opac_M02|30
25|0.8|80,47,58|1|205,65,112|HMF_HED_PROLash_Opac_M02|30
26|0.5|109,57,38|0.45|161,102,34|HMF_HED_PROLash_Opac_M02|30
27|1|109,57,38|0.9|161,102,34|HMF_HED_PROLash_Opac_M02|30
28|1|109,57,38|1.8|161,102,34|HMF_HED_PROLash_Opac_M02|30
29|0.4|55,60,28|0.3|121,200,60|HMF_HED_PROLash_Opac_M02|30
30|0.8|55,60,28|0.6|121,200,60|HMF_HED_PROLash_Opac_M02|30
31|0.8|55,60,28|1.2|121,200,60|HMF_HED_PROLash_Opac_M02|30
32|0.4|55,60,28|0.3|64,131,183|HMF_HED_PROLash_Opac_M02|30
33|0.8|55,60,28|0.6|64,131,183|HMF_HED_PROLash_Opac_M02|30
34|0.8|55,60,28|1.2|64,131,183|HMF_HED_PROLash_Opac_M02|30
""";

    private const string LipsData = """
1|0|0,0,0|0|0|50
2|1|133,91,62|7|0.3|10
3|1|133,91,62|0|0|10
4|1|169,122,89|0|0|10
5|1|159,107,100|0|0|10
6|1|197,143,162|7|0.3|10
7|1|197,143,162|0|0|10
8|1|190,124,133|0|0|10
9|1|157,111,124|0|0|10
10|1|155,87,106|0|0|10
11|1|156,77,83|7|0.3|10
12|1|156,77,83|0|0|10
13|1|237,145,210|0|0|10
14|1|216,104,146|0|0|10
15|1|186,80,95|0|0|10
16|1|165,71,84|7|0.3|10
17|1|165,71,84|0|0|10
18|1|169,52,52|0|0|10
19|1|150,47,52|0|0|10
20|1|135,20,52|7|0.3|10
21|1|135,20,52|0|0|10
22|1|125,31,86|0|0|10
23|1|139,89,138|0|0|10
24|1|152,75,161|0|0|10
25|1|102,55,109|7|0.3|10
26|1|102,55,109|0|0|10
27|1|64,131,183|0|0|10
28|1|92,96,149|0|0|10
29|1|83,68,115|7|0.3|10
30|1|83,68,115|0|0|10
31|1|43,33,33|0|0|10
32|1|0,0,0|7|0.3|10
""";

    public static Player2DaRandomisationResult Roll(Player2DaRandomisationRequest request, Random random)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(random);
        var output = new Dictionary<string, Player2DaValue>(StringComparer.OrdinalIgnoreCase);
        var selected = new Dictionary<Player2DaPalette, int>();
        var skinRows = ParseSkin();
        Row? skin = null;
        if (request.Palettes.Contains(Player2DaPalette.SkinTone))
        {
            skin = Pick(skinRows, random);
            selected[Player2DaPalette.SkinTone] = skin.Key;
            ApplySkin(skin, request, output);
        }
        else if (request.CurrentSkinRow is int currentSkin)
            skin = skinRows.FirstOrDefault(row => row.Key == currentSkin);
        else if (request.CurrentValues.TryGetValue("SkinTone", out var currentSkinValue) && currentSkinValue.Kind == Player2DaValueKind.Vector)
            skin = skinRows.FirstOrDefault(row => VectorMatches(currentSkinValue.Vector, Rgb(row.Fields[0])));

        if (request.Palettes.Contains(Player2DaPalette.HairColour))
        {
            // Hair and facial-hair presets are rolled from the full character-creator
            // palettes. Skin rows still constrain iris choices, while brow/beard
            // colours retain their own paired columns from the selected preset row.
            var hairRows = ParseHair();
            if (hairRows.Count > 0)
            {
                var hair = Pick(hairRows, random);
                selected[Player2DaPalette.HairColour] = hair.Key;
                ApplyHair(hair, request, output);
            }
        }

        if (request.Palettes.Contains(Player2DaPalette.FacialHairColour) &&
            (request.HasBrowTexture || request.HasBeardTexture))
        {
            var rows = ParseFacialHair();
            if (rows.Count > 0)
            {
                var row = Pick(rows, random);
                ApplyFacialHair(row, request, output);
                selected[Player2DaPalette.FacialHairColour] = row.Key;
            }
        }

        if (request.Palettes.Contains(Player2DaPalette.IrisColour))
        {
            var range = GetRange(skin, "iris");
            var rows = Constrain(ParseIris(), range);
            if (rows.Count > 0)
            {
                var row = Pick(rows, random);
                selected[Player2DaPalette.IrisColour] = row.Key;
                PutVector(output, request, "EYE_Iris_Colour_Vector", Rgb(row.Fields[0]));
            }
        }
        if (request.Palettes.Contains(Player2DaPalette.Blush))
        {
            var row = PickWeighted(ParseBlush(), random);
            selected[Player2DaPalette.Blush] = row.Key;
            PutScalar(output, request, "HED_Blush_Scalar", ParseFloat(row.Fields[0]));
            PutVector(output, request, "HED_Blush_Vector", Rgb(row.Fields[1]));
        }
        if (request.Palettes.Contains(Player2DaPalette.EyeMakeup))
        {
            var row = PickWeighted(ParseEye(), random);
            selected[Player2DaPalette.EyeMakeup] = row.Key;
            PutScalar(output, request, "HED_EyeShadow_Tint_Scalar", ParseFloat(row.Fields[0]));
            PutVector(output, request, "HED_EyeShadow_Tint_Vector", Rgb(row.Fields[1]));
            PutScalar(output, request, "HED_Brow_Tint_Scalar", ParseFloat(row.Fields[2]));
            PutVector(output, request, "HED_Brow_Tint_Vector", Rgb(row.Fields[3]));
            if (request.AllowedParameters.Contains("HED_Lash_Diff"))
                output["HED_Lash_Diff"] = Player2DaValue.FromTexture(row.Fields[4]);
        }
        if (request.Palettes.Contains(Player2DaPalette.Lips))
        {
            var row = PickWeighted(ParseLips(), random);
            selected[Player2DaPalette.Lips] = row.Key;
            PutScalar(output, request, "HED_Lips_Tint_Scalar", ParseFloat(row.Fields[0]));
            PutVector(output, request, "HED_Lips_Tint_Vector", Rgb(row.Fields[1]));
            PutScalar(output, request, "HED_Addn_SPwr_Lips_Scalar", ParseFloat(row.Fields[2]));
            PutScalar(output, request, "HED_Addn_Spec_Lips_Scalar", ParseFloat(row.Fields[3]));
        }
        return new Player2DaRandomisationResult(output, selected);
    }

    public static int GetRowCount(Player2DaPalette palette) => palette switch
    {
        Player2DaPalette.SkinTone => 28,
        Player2DaPalette.HairColour => 22,
        Player2DaPalette.FacialHairColour => 20,
        Player2DaPalette.IrisColour => 17,
        Player2DaPalette.Blush => 31,
        Player2DaPalette.EyeMakeup => 34,
        Player2DaPalette.Lips => 32,
        _ => 0
    };

    private static void ApplySkin(Row row, Player2DaRandomisationRequest request, Dictionary<string, Player2DaValue> output)
    {
        PutVector(output, request, "SkinTone", Rgb(row.Fields[0]));
        PutVector(output, request, "EYE_White_Colour_Vector", Rgb(row.Fields[1]));
        PutVector(output, request, "HED_Spec_Add_Vector", Rgb(row.Fields[2]));
        PutVector(output, request, "HED_Scar_Vector", Rgb(row.Fields[3]));
        PutVector(output, request, "HED_Teeth_Vector", Rgb(row.Fields[4]));
    }

    private static void ApplyHair(Row row, Player2DaRandomisationRequest request, Dictionary<string, Player2DaValue> output)
    {
        PutVector(output, request, "HED_Hair_Colour_Vector", Rgb(row.Fields[0]));
        var names = new[] { "HAIR_Shine_Desaturate_Scalar", "HED_Scalp_PhongSpec_Scalar", "Highlight1SpecExp_Scalar", "Highlight2SpecExp_Scalar", "Hair_Spec_Aniso_Exp_Scalar", "HAIR_Spec_Contribution_Scalar", "HAIR_SPwr_Scalar" };
        var indexes = new[] { 1, 2, 3, 4, 5, 6, 8 };
        for (var i = 0; i < names.Length; i++) PutScalar(output, request, names[i], ParseFloat(row.Fields[indexes[i]]));
    }

    private static void ApplyFacialHair(Row row, Player2DaRandomisationRequest request, Dictionary<string, Player2DaValue> output)
    {
        PutVector(output, request, "HED_Addn_Colour_Vector", Rgb(row.Fields[0]));
        PutVector(output, request, "blonde", Rgb(row.Fields[1]));
        PutScalar(output, request, "HED_Addn_Colour_02_Scalar", ParseFloat(row.Fields[2]));
        PutScalar(output, request, "HED_Addn_Blowout_Scalar", ParseFloat(row.Fields[3]));
    }

    private static List<Row> ParseSkin() => Parse(SkinData, 9, fields => new Dictionary<string, (double, double)>
    {
        ["hair"] = ParseRange(fields[5]), ["facial"] = ParseRange(fields[6]), ["iris"] = ParseRange(fields[7])
    });
    private static List<Row> ParseHair() => Parse(HairData, 10, fields => new Dictionary<string, (double, double)> { ["brow"] = ParseRange(fields[7]) });
    private static List<Row> ParseFacialHair() => Parse(FacialHairData, 5, _ => new Dictionary<string, (double, double)>());
    private static List<Row> ParseIris() => Parse(IrisData, 2, _ => new Dictionary<string, (double, double)>());
    private static List<Row> ParseBlush() => Parse(BlushData, 4, _ => new Dictionary<string, (double, double)>());
    private static List<Row> ParseEye() => Parse(EyeData, 7, _ => new Dictionary<string, (double, double)>());
    private static List<Row> ParseLips() => Parse(LipsData, 6, _ => new Dictionary<string, (double, double)>());

    private static Row? FindCurrentHairRow(IReadOnlyDictionary<string, Player2DaValue> currentValues)
    {
        if (!currentValues.TryGetValue("HED_Hair_Colour_Vector", out var current) || current.Kind != Player2DaValueKind.Vector)
            return null;
        return ParseHair().FirstOrDefault(row => VectorMatches(current.Vector, Rgb(row.Fields[0])));
    }

    private static List<Row> Parse(string data, int fieldCount, Func<string[], IReadOnlyDictionary<string, (double, double)>> ranges)
    {
        var result = new List<Row>();
        foreach (var line in data.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('|');
            if (parts.Length != fieldCount) throw new InvalidOperationException("Invalid embedded 2DA row.");
            var key = int.Parse(parts[0], CultureInfo.InvariantCulture);
            var fields = parts[1..];
            var weight = fields.Length > 0 && int.TryParse(fields[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedWeight) ? parsedWeight : 1;
            result.Add(new Row(key, weight, fields, ranges(fields)));
        }
        return result;
    }

    private static List<Row> Constrain(List<Row> rows, (double Min, double Max)? range)
    {
        if (range is null || range.Value is (-1, -1)) return rows;
        if (range.Value.Max < 1 || range.Value.Max < range.Value.Min) return [];
        return rows.Where(row => row.Key >= range.Value.Min && row.Key <= range.Value.Max).ToList();
    }

    private static (double Min, double Max)? GetRange(Row? row, string name) =>
        row is not null && row.Ranges.TryGetValue(name, out var range) ? range : null;

    private static (double Min, double Max)? IntersectRanges((double Min, double Max)? left, (double Min, double Max)? right)
    {
        if (left is null || left.Value == (-1, -1)) return right;
        if (right is null || right.Value == (-1, -1)) return left;
        return (Math.Max(left.Value.Min, right.Value.Min), Math.Min(left.Value.Max, right.Value.Max));
    }

    private static Row Pick(IReadOnlyList<Row> rows, Random random) => rows.Count == 0
        ? throw new InvalidOperationException("The 2DA range contains no eligible rows; caller should preserve its current value.")
        : rows[random.Next(rows.Count)];

    private static Row PickWeighted(IReadOnlyList<Row> rows, Random random)
    {
        var total = rows.Sum(row => Math.Max(0, row.Weight));
        var roll = random.Next(total);
        foreach (var row in rows)
        {
            roll -= Math.Max(0, row.Weight);
            if (roll < 0) return row;
        }
        return rows[^1];
    }

    private static void PutVector(Dictionary<string, Player2DaValue> output, Player2DaRandomisationRequest request, string name, Vector3 rgb)
    {
        if (!request.AllowedParameters.Contains(name)) return;
        var alpha = request.CurrentValues.TryGetValue(name, out var current) && current.Kind == Player2DaValueKind.Vector ? current.Vector.W : 1f;
        output[name] = Player2DaValue.FromVector(new Vector4(rgb, alpha));
    }

    private static void PutScalar(Dictionary<string, Player2DaValue> output, Player2DaRandomisationRequest request, string name, float value)
    {
        if (request.AllowedParameters.Contains(name)) output[name] = Player2DaValue.FromScalar(value);
    }

    private static Vector3 Rgb(string text)
    {
        var channels = text.Split(',').Select(value => byte.Parse(value, CultureInfo.InvariantCulture) / 255f)
            .Select(value => value <= 0.04045f ? value / 12.92f : MathF.Pow((value + 0.055f) / 1.055f, 2.4f)).ToArray();
        return new Vector3(channels[0], channels[1], channels[2]);
    }

    private static float ParseFloat(string value) => float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
    private static (double Min, double Max) ParseRange(string value)
    {
        var parts = value.Split(',');
        return (double.Parse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture), double.Parse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture));
    }

    private static bool VectorMatches(Vector4 current, Vector3 candidate) =>
        MathF.Abs(current.X - candidate.X) < 0.000001f &&
        MathF.Abs(current.Y - candidate.Y) < 0.000001f &&
        MathF.Abs(current.Z - candidate.Z) < 0.000001f;

}
