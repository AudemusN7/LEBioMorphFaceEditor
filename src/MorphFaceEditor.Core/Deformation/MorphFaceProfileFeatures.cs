using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.Core.Deformation;

/// <summary>Stored metadata and control eligibility shared by editing and corpus generation.</summary>
public static class MorphFaceProfileFeatures
{
    public static IReadOnlySet<string> HumanMaleMetadataOnlyFeatures { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Formal",
        "None",
        "Sarge",
        "Slick",
        "eyes_bagOut",
        "eyeShape_droop",
        "cheeks_gaunt",
        "eyeShape_liara",
        "eyeShape_iconic",
        "eyeShape_oldBlk",
        "eyeShape_yngAsn",
        "nose_BottomThin",
        "nose_BottomWide",
        "race_asnOld",
        "race_asnYoung",
        "race_blackOld",
        "race_Blackyng",
        "race_cauOld",
        "race_cauYng",
        "teeth_canineExtend",
        "teeth_Narrow",
        "teeth_Wide"
    };

    public static IReadOnlySet<string> HumanFemaleMetadataOnlyFeatures { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Stored by LE1's female character-creator metadata, but absent from
        // HMF_BaseMorphSet. Representative baked-face oracles prove that these
        // values do not contribute a direct target delta.
        "eyes_lashAngle",
        "eyes_lashLength",
        "eyes_bagOut",
        "cheeks_gaunt",
        "Eastwood",
        "eyes_Shape_droop",
        "eyes_Shape_sleepy",
        "eyes_Shape_wide",
        "HAIR_splitSide\"",
        "mouth_cheekMass",
        "neck_apple",
        "None",
        "pupil_Large",
        "pupil_Small",
        "rollins",
        "teeth_canineExtend",
        "teeth_close",
        "teeth_Narrow",
        "teeth_Seperate"
    };

    public static IReadOnlySet<string> Le3HumanMaleMetadataOnlyFeatures { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Present with non-zero weights across the 169-face LE3 HMM corpus,
        // absent from its 153-target HMM_BaseMorphSet, and proven inert by
        // exact reconstruction of every stored 2,392-vertex LOD0.
        "cheeks_gaunt",
        "eyes_bagOut",
        "eyes_Large",
        "eyeShape_droop",
        "eyeShape_sleepy",
        "Formal",
        "neck_thick",
        "None",
        "nose_BottomThin",
        "nose_BottomWide",
        "race_asnOld",
        "race_asnYoung",
        "race_cauOld",
        "race_cauYng",
        "Slick",
        "teeth_Narrow"
    };

    public static IReadOnlySet<string> Le3HumanFemaleMetadataOnlyFeatures { get; } =
        new HashSet<string>(HumanFemaleMetadataOnlyFeatures, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// LE1/2 male character creation serialises these two UI feature names even
    /// though their canonical HMM target objects use different names.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Le12HumanMaleFeatureAliases { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Jaw_Width"] = "jaw_wide",
            ["mouthShape_thick"] = "mouthShape_fatLips"
        };

    public static IReadOnlySet<string> AsariMetadataOnlyFeatures { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "eyes_PosDown",
            "eyes_Big",
            "jaw_narrow",
            "mouth_CornersDown",
            "nose_BridgeThin",
            "nose_TipUp",
            "race_iconic",
            "race_oldAsn",
            "race_oldBlk",
            "race_oldCauc",
            "race_yngAsn",
            "race_yngBlk",
            "race_yngCauc"
        };

    private static readonly IReadOnlySet<string> AsariHiddenFeatures =
        new HashSet<string>(AsariMetadataOnlyFeatures, StringComparer.OrdinalIgnoreCase)
        {
            "baseHead",
            "lashes"
        };

    public static IReadOnlySet<string> SalarianMetadataOnlyFeatures { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Present in official face recipes but absent from SAL_BaseMorphSet.
            // The corpus oracle establishes whether it is inert front-end metadata.
            "shape_chubby"
        };

    private static readonly IReadOnlySet<string> SalarianHiddenFeatures =
        new HashSet<string>(SalarianMetadataOnlyFeatures, StringComparer.OrdinalIgnoreCase)
        {
            // An internal seam correction, retained and evaluated but not presented
            // as an authored facial control.
            "neck_correction"
        };

    public static IReadOnlySet<string> TurianMetadataOnlyFeatures { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "eyes_Large" };

    public static IReadOnlySet<string> TurianAllFeatureNames { get; } =
        new HashSet<string>(TurianMetadataOnlyFeatures, StringComparer.OrdinalIgnoreCase)
    {
        "mandible_short",
        "mandible_long",
        "mandible_retract",
        "mandible_extend",
        "mandible_Flare",
        "mandible_Rotate",
        "mandible_Thick",
        "head_ScaleUp",
        "headSpikes_Short",
        "headSpikes_Long",
        "cheeks_Down",
        "cheeks_Up",
        "neck_Thin",
        "brow_Back",
        "brow_Forward",
        "brow_Down",
        "brow_Up",
        "eyes_Back",
        "eyes_Forward",
        "eyes_small",
        "eyes_Big",
        "eyes_narrow",
        "eyes_Wide",
        "nose_Down",
        "nose_Up",
        "nose_In",
        "nose_Out",
        "nose_Short",
        "nose_Long",
        "nose_Narrow",
        "nose_Wide",
        "mouth_Back",
        "mouth_Forward",
        "mouth_Down",
        "mouth_Up",
        "mouth_Narrow",
        "mouth_Wide",
    };

    public static IReadOnlySet<string> KroganMetadataOnlyFeatures { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "race_asnOld",
            "race_asnYoung",
            "race_blackOld",
            "race_Blackyng",
            "race_cauOld",
            "race_cauYng"
        };

    public static IReadOnlySet<string> BatarianMetadataOnlyFeatures { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "jaw_narrow",
            "shape_skinny"
        };

    private static readonly IReadOnlySet<string> HumanMaleHiddenFeatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Proven zero-effect or internal construction/corrective targets.
        "averageHead", "baseHead", "BONE_blinkFix", "DEBUG_EyeLidCorrector", "DEBUG_InnerEyeCorrector",
        "eyeShape_droop", "eye_Shape_droop",
        // Composite/duplicate variants superseded by the explicit controls in the profile.
        "BuzzCut", "BuzzCut_WidowsPeak", "flatTop_WidowsPeak", "objobjWillis", "Willis01",
        // Character-creator metadata with no live geometry. Formal/Sarge/Slick are retained
        // because they are explicitly part of the requested hairstyle surface.
        "None", "Formal", "Sarge", "Slick", "lashes", "eyes_bagOut", "cheeks_gaunt", "eyeShape_liara", "eyeShape_iconic",
        "eyeShape_oldBlk", "eyeShape_yngAsn", "nose_BottomThin", "nose_BottomWide",
        "race_asnOld", "race_asnYoung", "race_blackOld", "race_Blackyng", "race_cauOld", "race_cauYng",
        "teeth_canineExtend", "teeth_Narrow", "teeth_Wide"
    };

    private static readonly IReadOnlySet<string> HumanFemaleHiddenFeatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "averageHead",
        "BONE_blinkFix",
        "Eastwood",
        "rollins",
        "HAIR_splitSide",
        "HAIR_splitSide\"",
        "None",
        "mouth_cheekMass",
        "cheeks_gaunt",
        "eyeShape_droop",
        "eyes_Shape_droop",
        "eyes_Shape_sleepy",
        "eyes_Shape_wide",
        "pupil_Small",
        "pupil_Large",
        "eyes_bagOut",
        "eyes_lashAngle",
        "eyes_lashLength",
        "teeth_close",
        "teeth_Seperate",
        "teeth_canineExtend",
        "teeth_Narrow",
        "neck_apple"
    };

    private static readonly IReadOnlySet<string> Le3HumanMaleHiddenFeatures =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "eyes_Large",
            "eyeShape_droop",
            "eyeShape_sleepy"
        };

    public static IReadOnlyDictionary<string, string> NoAliases { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    // These names remain evaluated/preserved when authored, but do not become controls or random donors.
    public static bool IsControlEligible(string archetype, MorphFaceGame game, string name, MorphTargetAsset? target = null) =>
        archetype switch
        {
            "human-male" => !HumanMaleHiddenFeatures.Contains(name) &&
                            (game != MorphFaceGame.LE3 || !Le3HumanMaleHiddenFeatures.Contains(name)),
            "human-female" => !HumanFemaleHiddenFeatures.Contains(name) &&
                              (game != MorphFaceGame.LE3 || !IsUnsupportedLe3FemaleHair(name, target)),
            "asari" => !AsariHiddenFeatures.Contains(name),
            "salarian" => !SalarianHiddenFeatures.Contains(name),
            "turian" => !TurianMetadataOnlyFeatures.Contains(name),
            "batarian" => !BatarianMetadataOnlyFeatures.Contains(name) &&
                          !name.Equals("teeth_correction", StringComparison.OrdinalIgnoreCase),
            "krogan" => !KroganMetadataOnlyFeatures.Contains(name),
            "female-turian" or "female-batarian" or "vorcha" or "detached-mesh" => false,
            _ => true
        };

    public static bool IsUnsupportedLe3FemaleHair(string name, MorphTargetAsset? target) =>
        name.StartsWith("HAIR_", StringComparison.OrdinalIgnoreCase) &&
        target?.Lods.Any(lod => lod.LodIndex == 0 && lod.Vertices.Count == 0) == true &&
        target.Lods.Any(lod => lod.LodIndex > 0 && lod.Vertices.Count > 0);
}
