using System.Runtime.InteropServices;
using MorphFaceEditor.Core.Materials;
using SharpDX;

namespace MorphFaceEditor.Rendering;

internal readonly record struct HeadPreviewMaterialBindings(
    HeadPreviewTexture? Diffuse,
    HeadPreviewTexture? Normal,
    HeadPreviewTexture? Mask,
    HeadPreviewTexture? Detail,
    HeadPreviewTexture? Auxiliary1,
    HeadPreviewTexture? Auxiliary2,
    HeadPreviewTexture? Auxiliary3,
    HeadPreviewTexture? Auxiliary4)
{
    public static HeadPreviewMaterialBindings Create(HeadPreviewMaterial material)
    {
        if (material.Family == HeadMaterialFamily.MaskedHair)
        {
            return new HeadPreviewMaterialBindings(
                SelectByName(material, "__PROShort01_Diffuse"),
                null,
                SelectByName(material, "__PROShort01_Opacity"),
                SelectByName(material, "__PROShort01_Tangent"),
                SelectByName(material, "__PROShort01_Specular"),
                null, null, null);
        }

        if (material.Family is HeadMaterialFamily.Hair or HeadMaterialFamily.Lashes)
        {
            // The supplied LE1 hair and lash pixel permutations compile to one
            // texture uniform apiece. HAIR_Norm/Mask/Tang/SpecShift remain in
            // package data for round-trip fidelity, but are dead in this master.
            return new HeadPreviewMaterialBindings(
                Select(material, PrimaryDiffuseName(material), TextureRole.Diffuse),
                null, null, null, null, null, null, null);
        }

        return new HeadPreviewMaterialBindings(
            Select(material, PrimaryDiffuseName(material), TextureRole.Diffuse),
            Select(material, PrimaryNormalName(material), TextureRole.Normal),
            Select(material, PrimaryMaskName(material.Family), TextureRole.Mask, TextureRole.Specular),
            Select(material, PrimaryDetailName(material.Family), TextureRole.Detail, TextureRole.Tangent),
            SelectByName(material, Auxiliary1Name(material.Family)),
            SelectByName(material, Auxiliary2Name(material.Family)),
            SelectByName(material, Auxiliary3Name(material.Family)),
            SelectByName(material, Auxiliary4Name(material.Family)));
    }

    private static HeadPreviewTexture? Select(
        HeadPreviewMaterial material,
        string? preferredName,
        params TextureRole[] roles)
    {
        var preferred = SelectByName(material, preferredName);
        return preferred ?? material.Textures.Values.FirstOrDefault(texture =>
            roles.Contains(texture.Role) && IsSupported(material, texture.ParameterName, MaterialParameterKind.Texture));
    }

    private static HeadPreviewTexture? SelectByName(HeadPreviewMaterial material, string? name) =>
        name is not null &&
        material.Textures.TryGetValue(name, out var texture) &&
        IsSupported(material, name, MaterialParameterKind.Texture)
            ? texture
            : null;

    private static bool IsSupported(
        HeadPreviewMaterial material,
        string name,
        MaterialParameterKind kind) =>
        name.StartsWith("__", StringComparison.Ordinal) || material.Supports(name, kind);

    private static string? PrimaryDiffuseName(HeadPreviewMaterial material) => material.Family switch
    {
        HeadMaterialFamily.VorchaSkin => "TUR_HED_Diff",
        HeadMaterialFamily.VorchaEyes => "ALN_HED_Diff",
        HeadMaterialFamily.Skin => "HED_Diff",
        HeadMaterialFamily.AsariSkin => "ASA_HED_Diff",
        HeadMaterialFamily.SalarianSkin => "SAL_HED_Diff",
        HeadMaterialFamily.SalarianEyes => "SAL_HED_EYE_Diff",
        HeadMaterialFamily.TurianSkin => "TUR_HED_Diff",
        HeadMaterialFamily.TurianEyes when material.IsLe3 => "EYE_Diff",
        HeadMaterialFamily.TurianEyes => "TUR_EYE_Diff",
        HeadMaterialFamily.BatarianSkin => "BAT_HED_Diff",
        HeadMaterialFamily.KroganSkin => "KRO_HED_Diff",
        HeadMaterialFamily.KroganEyes => "KRO_EYE_Diff",
        HeadMaterialFamily.Scalp => "HED_Scalp_Diff",
        HeadMaterialFamily.Eyes => "EYE_Diff",
        HeadMaterialFamily.Lashes => "HED_Lash_Diff",
        HeadMaterialFamily.Hair when material.Textures.ContainsKey("HAIR_ADDN_Diff") => "HAIR_ADDN_Diff",
        HeadMaterialFamily.Hair => "HAIR_Diff",
        _ => null
    };

    private static string? PrimaryNormalName(HeadPreviewMaterial material) => material.Family switch
    {
        HeadMaterialFamily.VorchaSkin => "ALN_HED_Norm",
        HeadMaterialFamily.VorchaEyes => "Eye_Norm",
        HeadMaterialFamily.Skin => "HED_Norm",
        HeadMaterialFamily.AsariSkin => "ASA_HED_Norm",
        HeadMaterialFamily.SalarianSkin => "SAL_HED_Norm",
        HeadMaterialFamily.SalarianEyes => "SAL_HED_EYE_Norm",
        HeadMaterialFamily.TurianSkin => "TUR_HED_Norm",
        HeadMaterialFamily.TurianEyes when material.IsLe3 => "Eye_Norm",
        HeadMaterialFamily.TurianEyes => "TUR_EYE_Iris_Norm",
        HeadMaterialFamily.BatarianSkin => "BAT_HED_Norm",
        HeadMaterialFamily.KroganSkin => "KRO_HED_Norm",
        HeadMaterialFamily.KroganEyes => "KRO_EYE_Iris_Norm",
        HeadMaterialFamily.Scalp => "HED_Scalp_Norm",
        HeadMaterialFamily.Eyes => "EYE_Iris_Norm",
        _ => null
    };

    private static string? PrimaryMaskName(HeadMaterialFamily family) => family switch
    {
        HeadMaterialFamily.Skin => "HED_Mask",
        HeadMaterialFamily.AsariSkin => "ASA_HED_Mask",
        HeadMaterialFamily.SalarianSkin => "SAL_HED_Mask",
        HeadMaterialFamily.SalarianEyes => "SAL_HED_EYE_Spec",
        HeadMaterialFamily.TurianSkin => "TUR_HED_Mask",
        HeadMaterialFamily.TurianEyes => "TUR_EYE_Mask",
        HeadMaterialFamily.BatarianSkin => "BAT_HED_Mask",
        HeadMaterialFamily.KroganSkin => "KRO_HED_Mask",
        HeadMaterialFamily.KroganEyes => "KRO_Eye_Mask",
        HeadMaterialFamily.VorchaSkin => "ALN_HED_Tint",
        HeadMaterialFamily.Scalp => "HED_Scalp_Spec",
        HeadMaterialFamily.Eyes => "EYE_Mask",
        _ => null
    };

    private static string? PrimaryDetailName(HeadMaterialFamily family) => family switch
    {
        HeadMaterialFamily.Skin => "HED_Addn",
        HeadMaterialFamily.AsariSkin => "ASA_HED_Addn",
        HeadMaterialFamily.SalarianSkin => "SAL_HED_Addn",
        HeadMaterialFamily.TurianSkin => "TUR_HED_Addn",
        HeadMaterialFamily.TurianEyes => "TUR_Eye_Spec",
        HeadMaterialFamily.BatarianSkin => "BAT_HED_Addn",
        HeadMaterialFamily.KroganSkin => "KRO_HED_Addn",
        HeadMaterialFamily.KroganEyes => "KRO_Eye_Spec",
        HeadMaterialFamily.VorchaSkin => "ALN_HED_Tatt",
        HeadMaterialFamily.Scalp => "HED_Tang",
        _ => null
    };

    private static string? Auxiliary1Name(HeadMaterialFamily family) => family switch
    {
        HeadMaterialFamily.Skin => "HED_Frek",
        HeadMaterialFamily.AsariSkin => "ASA_HED_MakeUp",
        HeadMaterialFamily.SalarianSkin => "SAL_HED_Tint",
        HeadMaterialFamily.TurianSkin => "TUR_HED_Tint",
        HeadMaterialFamily.TurianEyes => "TUR_EYE_Lens_Norm",
        HeadMaterialFamily.BatarianSkin => "BAT_HED_Tint",
        HeadMaterialFamily.KroganSkin => "KRO_HED_Tint",
        HeadMaterialFamily.KroganEyes => "KRO_EYE_Lens_Norm",
        HeadMaterialFamily.Scalp => "HED_Teeth_Diff",
        HeadMaterialFamily.Eyes => "EYE_Lens_Norm",
        _ => null
    };

    private static string? Auxiliary2Name(HeadMaterialFamily family) => family switch
    {
        HeadMaterialFamily.Skin => "HED_Norm_02",
        HeadMaterialFamily.AsariSkin => "ASA_HED_Tatt",
        HeadMaterialFamily.SalarianSkin => "SAL_HED_Tatt",
        HeadMaterialFamily.TurianSkin => "TUR_HED_Tatt",
        HeadMaterialFamily.BatarianSkin => "BAT_HED_Spec",
        HeadMaterialFamily.KroganSkin => "KRO_HED_Tnt2",
        HeadMaterialFamily.Scalp => "HED_Scalp_SpecShift",
        _ => null
    };

    private static string? Auxiliary3Name(HeadMaterialFamily family) => family switch
    {
        HeadMaterialFamily.Skin => "HED_Makeup_Mask",
        HeadMaterialFamily.AsariSkin => "__ASA_SkinNoise",
        HeadMaterialFamily.SalarianSkin => "SAL_HED_SpecMap",
        HeadMaterialFamily.Scalp => "HED_Scalp_SpecShift2",
        _ => null
    };

    private static string? Auxiliary4Name(HeadMaterialFamily family) => family switch
    {
        HeadMaterialFamily.AsariSkin => "__ASA_SpecMultiplierMask",
        _ => null
    };
}

[StructLayout(LayoutKind.Sequential)]
internal struct HeadPreviewMaterialConstants
{
    public Vector4 BaseColor;
    public Vector4 SecondaryColor;
    public Vector4 TertiaryColor;
    public Vector4 QuaternaryColor;
    public Vector4 SpecularColor;
    public Vector4 ScatterColor;
    public Vector4 FreckleRedColor;
    public Vector4 FreckleGreenColor;
    public Vector4 FreckleBlueColor;
    public Vector4 BlondeColor;
    public Vector4 TransmissionColor;
    public Vector4 SurfaceParameters;
    public Vector4 TextureFlags0;
    public Vector4 TextureFlags1;
    public Vector4 GeneralParameters;
    public Vector4 SkinParameters0;
    public Vector4 SkinParameters1;
    public Vector4 SkinParameters2;
    public Vector4 SkinParameters3;
    public Vector4 ScalpParameters0;
    public Vector4 ScalpParameters1;
    public Vector4 ScalpParameters2;
    public Vector4 EyeParameters;
    public Vector4 EyeParameters1;
    public Vector4 EyeParameters2;
    public Vector4 EyeEmissiveColor;
    public Vector4 BlushColor;
    public Vector4 FemaleParameters;

    public static HeadPreviewMaterialConstants Create(
        HeadPreviewMaterial material,
        HeadPreviewRenderMode mode,
        HeadPreviewMaterialBindings bindings)
    {
        var diagnostic = mode == HeadPreviewRenderMode.Diagnostic;
        var family = FamilyIndex(material.Family);
        var femaleFace = material.Family == HeadMaterialFamily.Skin &&
            (material.Textures.ContainsKey("HED_Makeup_Mask") ||
             material.Scalars.ContainsKey("HED_Brow_Tint_Scalar") ||
             material.Vectors.ContainsKey("HED_Lips_Tint_Vector"));
        if (material.Family == HeadMaterialFamily.AsariSkin)
        {
            return CreateAsari(material, diagnostic, family, bindings);
        }
        if (material.Family == HeadMaterialFamily.SalarianSkin)
        {
            return CreateSalarianSkin(material, diagnostic, family, bindings);
        }
        if (material.Family == HeadMaterialFamily.SalarianEyes)
        {
            return CreateSalarianEyes(material, diagnostic, family, bindings);
        }
        if (material.Family == HeadMaterialFamily.TurianSkin)
        {
            return CreateTurianSkin(material, diagnostic, family, bindings);
        }
        if (material.Family == HeadMaterialFamily.TurianEyes)
        {
            return CreateTurianEyes(material, diagnostic, family, bindings);
        }
        if (material.Family == HeadMaterialFamily.KroganSkin)
        {
            return CreateKroganSkin(material, diagnostic, family, bindings);
        }
        if (material.Family == HeadMaterialFamily.BatarianSkin)
        {
            return CreateBatarianSkin(material, diagnostic, family, bindings);
        }
        if (material.Family == HeadMaterialFamily.KroganEyes)
        {
            return CreateKroganEyes(material, diagnostic, family, bindings);
        }
        if (material.Family == HeadMaterialFamily.VorchaSkin)
        {
            return CreateVorchaSkin(material, diagnostic, family, bindings);
        }
        if (material.Family == HeadMaterialFamily.VorchaEyes)
        {
            return CreateVorchaEyes(material, diagnostic, family, bindings);
        }
        return new HeadPreviewMaterialConstants
        {
            BaseColor = diagnostic ? new Vector4(DiagnosticColor(material.Family), 1) : ResolveBaseColor(material),
            SecondaryColor = ResolveSecondaryColor(material),
            TertiaryColor = femaleFace
                ? GetVector(material, "HED_Brow_Tint_Vector", Vector4.Zero)
                : ResolveTertiaryColor(material),
            QuaternaryColor = femaleFace
                ? GetVector(material, "HED_EyeShadow_Tint_Vector", Vector4.Zero)
                : ResolveQuaternaryColor(material),
            SpecularColor = GetVector(material, "HED_Spec_Add_Vector", new Vector4(0.22f, 0.22f, 0.22f, 1)),
            ScatterColor = material.Family == HeadMaterialFamily.Eyes
                ? GetVector(material, "EyeLightScattering", new Vector4(0.2f, 0.1f, 0.1f, 1))
                : GetVector(material, "SkinLightScattering", new Vector4(0.45f, 0.05f, 0.03f, 1)),
            FreckleRedColor = GetVector(material, "HED_Frek_RedChannel_Vector", Vector4.Zero),
            FreckleGreenColor = GetVector(material, "HED_Frek_GreenChannel_Vector", Vector4.Zero),
            FreckleBlueColor = femaleFace
                ? GetVector(material, "HED_Lips_Tint_Vector", Vector4.Zero)
                : GetVector(material, "HED_Frek_BlueChannel_Vector", Vector4.Zero),
            BlondeColor = GetVector(material, "blonde", Vector4.Zero),
            TransmissionColor = material.Family == HeadMaterialFamily.Eyes
                ? GetVector(material, "Tmission_Color", Vector4.Zero)
                : GetVector(material, "HED_TClr_Vector", Vector4.Zero),
            SurfaceParameters = new Vector4(DefaultRoughness(material.Family), diagnostic ? 1 : 0, family,
                material.BlendMode == HeadMaterialBlendMode.Masked ? 0.33f : 0),
            TextureFlags0 = new Vector4(
                bindings.Diffuse is null ? 0 : 1,
                bindings.Normal is null ? 0 : 1,
                bindings.Mask is null ? 0 : 1,
                bindings.Detail is null ? 0 : 1),
            TextureFlags1 = new Vector4(
                bindings.Auxiliary1 is null ? 0 : 1,
                material.Family == HeadMaterialFamily.Eyes
                    ? material.FixedCubeTexture is null ? 0 : 1
                    : bindings.Auxiliary2 is null ? 0 : 1,
                material.Family == HeadMaterialFamily.Eyes
                    ? material.SecondaryFixedCubeTexture is null ? 0 : 1
                    : bindings.Auxiliary3 is null ? 0 : 1,
                bindings.Diffuse?.HasMeaningfulAlpha == true ? 1 : 0),
            GeneralParameters = new Vector4(
                GetScalar(material, "HED_TMis_Scalar", 1),
                GetScalar(material, "HED_SPwr_Scalar", 5),
                femaleFace
                    ? GetScalar(material, "HED_Lips_Tint_Scalar", 0)
                    : GetScalar(material, "HED_Mask_Scalar", 0),
                GetScalar(material, "HED_Scar_Scalar", 0)),
            SkinParameters0 = new Vector4(
                GetScalar(material, "HED_Norm_Blend", 0),
                GetScalar(material, "HED_Addn_Blend_Scalar", 0),
                femaleFace
                    ? GetScalar(material, "HED_Brow_Tint_Scalar", 0)
                    : GetScalar(material, "HED_Addn_Add_Scalar", 0),
                femaleFace
                    ? GetScalar(material, "HED_EyeShadow_Tint_Scalar", 0)
                    : GetScalar(material, "HED_Addn_Multiply_Scalar", 0)),
            SkinParameters1 = new Vector4(
                GetScalar(material, "HED_Addn_Blowout_Scalar", 1),
                GetScalar(material, "HED_Addn_Colour_02_Scalar", 1),
                femaleFace
                    ? GetScalar(material, "HED_Spec_NoBrow", 0)
                    : GetScalar(material, "HED_Brow_FadeOut_Scalar", 0),
                femaleFace
                    ? GetScalar(material, "HED_Addn_Spec_Lips_Scalar", 0)
                    : GetScalar(material, "HED_Addn_Spec_Add_Scalar", 0)),
            SkinParameters2 = new Vector4(
                GetScalar(material, "HED_Frek_RedChannel_Scalar", 0),
                GetScalar(material, "HED_Frek_GreenChannel_Scalar", 0),
                femaleFace
                    ? GetScalar(material, "HED_Addn_SPwr_Add_Scalar", 0)
                    : GetScalar(material, "HED_Frek_BlueChannel_Scalar", 0),
                femaleFace
                    ? GetScalar(material, "HED_Addn_SPwr_Lips_Scalar", 0)
                    : GetScalar(material, "HED_Addn_SPwr_Add_Scalar", 0)),
            SkinParameters3 = new Vector4(
                GetScalar(material, "HED_Lash_Opac_Scalar", 1),
                GetScalar(material, "HED_Lash_Spec_Scalar", 0),
                material.Family == HeadMaterialFamily.Hair && material.Textures.ContainsKey("HAIR_ADDN_Diff")
                    ? 1
                    : GetScalar(material, "HED_Addn_Colour_Blend_Scalar", 0),
                femaleFace ? 1 : 0),
            ScalpParameters0 = new Vector4(
                GetScalar(material, "HED_Scalp_Mask_Scalar", 1),
                GetScalar(material, "HED_Scalp_BuzzCut_Alpha_Scalar", 0),
                GetScalar(material, "HED_Scalp_Mask_OverlayKill_Scalar", 1),
                GetScalar(material, "HAIR_Mask_Alpha_Scalar", 0)),
            ScalpParameters1 = new Vector4(
                GetScalar(material, "Mask", 1),
                GetScalar(material, "HED_Teeth_Scalar", 0.75f),
                GetScalar(material, "HED_Scalp_PhongSpec_Scalar", 1),
                GetScalar(material, "HED_Spec_Aniso_Exp_Scalar", 3)),
            ScalpParameters2 = material.Family == HeadMaterialFamily.Hair && material.IsLe3
                ? new Vector4(
                    GetScalar(material, "Highlight1SpecExp_Scalar", 50),
                    GetScalar(material, "Hightlight1Intensity", 1),
                    GetScalar(material, "Highlight2SpecExp_Scalar", 250),
                    GetScalar(material, "Hightlight2Intensity", 1))
                : new Vector4(
                    GetScalar(material, "HAIR_Shine_Desaturate_Scalar", 0),
                    GetScalar(material, "Highlight1SpecExp_Scalar", 50),
                    GetScalar(material, "Highlight2SpecExp_Scalar", 250),
                    material.Supports("HED_Teeth_Vector", MaterialParameterKind.Vector) ? 1 : 0),
            EyeParameters = new Vector4(
                GetScalar(material, "U_Offset", 0.0642f),
                GetScalar(material, "V_Offset", -0.125f),
                GetScalar(material, "HED_EYE_FX_Scalar", 0),
                material.Supports("EYE_Lens_Norm", MaterialParameterKind.Texture) ? 1 : 0),
            EyeParameters1 = new Vector4(
                GetScalar(material, "X_Tile", 0.9f),
                GetScalar(material, "Y_Tile", 1.2f),
                GetScalar(material, "Emis_Scalar", 0),
                GetScalar(material, "Iris_Colour_Multiplier", 1)),
            EyeParameters2 = new Vector4(
                GetScalar(material, "Primary_Reflection_Multiplier", 1),
                GetScalar(material, "Secondary_Reflection_Multiplier", 1),
                GetScalar(material, "Sclera_Darken", 0.5f),
                material.IsLe3 ? 1 : 0),
            EyeEmissiveColor = GetVector(material, "Emis_Color", Vector4.Zero),
            BlushColor = femaleFace
                ? GetVector(material, "HED_Blush_Vector", Vector4.One)
                : Vector4.One,
            FemaleParameters = new Vector4(
                femaleFace ? GetScalar(material, "HED_Blush_Scalar", 0) : 0,
                0, 0, 0)
        };
    }

    private static HeadPreviewMaterialConstants CreateAsari(
        HeadPreviewMaterial material,
        bool diagnostic,
        float family,
        HeadPreviewMaterialBindings bindings) => new()
    {
        BaseColor = diagnostic ? new Vector4(DiagnosticColor(material.Family), 1)
            : GetVector(material, "SkinTone", new Vector4(0.1f, 0.2f, 0.7f, 1)),
        SecondaryColor = GetVector(material, "ASA_HED_Diffuse_02_Colour", Vector4.Zero),
        TertiaryColor = GetVector(material, "ASA_HED_Addn_Colour", Vector4.One),
        QuaternaryColor = GetVector(material, "ASA_HED_Tatt_Colour", Vector4.Zero),
        SpecularColor = GetVector(material, "ASA_HED_Spec_Add", new Vector4(0.535642f, 0.535642f, 0.535642f, 1)),
        ScatterColor = GetVector(material, "SkinLightScattering", new Vector4(0.0477758f, 0.00143313f, 0.0986892f, 1)),
        FreckleRedColor = GetVector(material, "ASA_HED_MakeUp_Eyes", new Vector4(0.103634f, 0.103634f, 0.103634f, 0)),
        FreckleGreenColor = GetVector(material, "ASA_HED_MakeUp_Lips", new Vector4(0.163641f, 0.163641f, 0.163641f, 0)),
        FreckleBlueColor = GetVector(material, "ASA_HED_Makeup_Blender_Vector", Vector4.One),
        BlondeColor = GetVector(material, "ASA_HED_Addn_Mask_Vector", new Vector4(0, 1, 0, 1)),
        TransmissionColor = GetVector(material, "ASA_HED_TClr_Tint", Vector4.Zero),
        SurfaceParameters = new Vector4(0.42f, diagnostic ? 1 : 0, family, 0),
        TextureFlags0 = new Vector4(
            bindings.Diffuse is null ? 0 : 1,
            bindings.Normal is null ? 0 : 1,
            bindings.Mask is null ? 0 : 1,
            bindings.Detail is null ? 0 : 1),
        TextureFlags1 = new Vector4(
            bindings.Auxiliary1 is null ? 0 : 1,
            bindings.Auxiliary2 is null ? 0 : 1,
            bindings.Auxiliary3 is null ? 0 : 1,
            bindings.Auxiliary4 is null ? 0 : 1),
        GeneralParameters = new Vector4(
            GetScalar(material, "ASA_HED_TMis_Switch", 0),
            GetScalar(material, "ASA_HED_SPwr_Multiplier_Scalar", 3),
            GetScalar(material, "ASA_HED_Diffuse_02_Colour_Scalar", 0),
            GetScalar(material, "ASA_HED_Face_Fresnel_Scalar", 0)),
        SkinParameters0 = new Vector4(
            GetScalar(material, "ASA_HED_Addn_Mask_Scalar", 0),
            GetScalar(material, "ASA_HED_Addn_Colour_Scalar", 0),
            GetScalar(material, "ASA_HED_MakeUp_Switch_Scalar", 0),
            GetScalar(material, "ASA_HED_Lip_Gloss_Scalar", 0.1f)),
        SkinParameters1 = new Vector4(
            GetScalar(material, "ASA_HED_Tatt_01_Scalar", 0),
            GetScalar(material, "ASA_HED_Tatt_02_Scalar", 0),
            GetScalar(material, "ASA_HED_Tatt_Blender_Scalar", 0),
            GetScalar(material, "ASA_HED_SPwr_Add_Scalar", 0)),
        SkinParameters2 = GetVector(material, "ASA_HED_Tatt_01_Vector", Vector4.Zero),
        SkinParameters3 = GetVector(material, "ASA_HED_Tatt_02_Vector", Vector4.Zero),
        ScalpParameters0 = GetVector(material, "ASA_HED_Tatt_01", Vector4.Zero),
        ScalpParameters1 = GetVector(material, "ASA_HED_Tatt_02", Vector4.Zero),
        ScalpParameters2 = new Vector4(GetScalar(material, "Mask", 1), 0, 0, 0),
        EyeParameters2 = new Vector4(0, 0, 0, material.IsLe3 ? 1 : 0),
        EyeEmissiveColor = GetVector(material, "ASA_HED_Teeth_Colour_Vector", Vector4.One)
    };

    private static HeadPreviewMaterialConstants CreateSalarianSkin(
        HeadPreviewMaterial material,
        bool diagnostic,
        float family,
        HeadPreviewMaterialBindings bindings) => new()
    {
        BaseColor = diagnostic ? new Vector4(DiagnosticColor(material.Family), 1)
            : GetVector(material, "SkinTone", new Vector4(0.42f, 0.55f, 0.34f, 1)),
        SecondaryColor = GetVector(material, "SAL_HED_Addn_Colour", Vector4.One),
        TertiaryColor = GetVector(material, "SAL_HED_Diff_02_Colour", Vector4.One),
        QuaternaryColor = GetVector(material, "SAL_HED_Tatt_Colour", Vector4.Zero),
        SpecularColor = GetVector(material, "SAL_HED_Spec_Colour", new Vector4(0.12f, 0.12f, 0.12f, 1)),
        ScatterColor = GetVector(material, "SkinLightScattering", new Vector4(1, 0.3f, 0.019f, 1)),
        FreckleRedColor = GetVector(material, "SAL_HED_Addn_Mask_Vector", new Vector4(0, 1, 0, 1)),
        FreckleGreenColor = GetVector(material, "SAL_HED_Addn_Spec_Colour", new Vector4(0.051122f, 0.078057f, 0.098689f, 0)),
        TransmissionColor = GetVector(material, "SAL_HED_Tmis_COLOUR", new Vector4(0.907547f, 0.254916f, 0.103634f, 0)),
        SurfaceParameters = new Vector4(0.42f, diagnostic ? 1 : 0, family, 0),
        TextureFlags0 = new Vector4(
            bindings.Diffuse is null ? 0 : 1,
            bindings.Normal is null ? 0 : 1,
            bindings.Mask is null ? 0 : 1,
            bindings.Detail is null ? 0 : 1),
        TextureFlags1 = new Vector4(
            bindings.Auxiliary1 is null ? 0 : 1,
            bindings.Auxiliary2 is null ? 0 : 1,
            bindings.Auxiliary3 is null ? 0 : 1,
            0),
        GeneralParameters = new Vector4(
            GetScalar(material, "SAL_HED_Addn_Mask_Scalar", 0),
            GetScalar(material, "SAL_HED_Addn_Blend_Scalar", 0),
            GetScalar(material, "SAL_HED_Diffuse02_Scalar", 0),
            GetScalar(material, "SAL_HED_Spec_Scalar", 0.35f)),
        SkinParameters0 = new Vector4(
            GetScalar(material, "SAL_HED_Tatt_01_Scalar", 0),
            GetScalar(material, "SAL_HED_Tatt_02_Scalar", 0),
            GetScalar(material, "SAL_HED_Addn_Spec_Scalar", 0),
            0),
        SkinParameters1 = GetVector(material, "SAL_HED_Tatt_01_Vector", Vector4.Zero),
        SkinParameters2 = GetVector(material, "SAL_HED_Tatt_02_Vector", Vector4.Zero),
        SkinParameters3 = GetVector(material, "SAL_HED_Tatt_01", Vector4.Zero),
        ScalpParameters0 = GetVector(material, "SAL_HED_Tatt_02", Vector4.Zero),
        EyeParameters2 = new Vector4(0, 0, 0, material.IsLe3 ? 1 : 0)
    };

    private static HeadPreviewMaterialConstants CreateSalarianEyes(
        HeadPreviewMaterial material,
        bool diagnostic,
        float family,
        HeadPreviewMaterialBindings bindings) => new()
    {
        BaseColor = diagnostic ? new Vector4(DiagnosticColor(material.Family), 1)
            : GetVector(material, "SAL_HED_EYE_Iris_Vector", new Vector4(0.12f, 0.2f, 0.18f, 1)),
        SecondaryColor = GetVector(material, "SAL_HED_EYE_Pupil_Vector", new Vector4(0.01f, 0.01f, 0.015f, 1)),
        SpecularColor = Vector4.One,
        SurfaceParameters = new Vector4(0.16f, diagnostic ? 1 : 0, family, 0),
        TextureFlags0 = new Vector4(
            bindings.Diffuse is null ? 0 : 1,
            bindings.Normal is null ? 0 : 1,
            bindings.Mask is null ? 0 : 1,
            0),
        TextureFlags1 = new Vector4(
            material.FixedCubeTexture is null ? 0 : 1,
            material.SecondaryFixedCubeTexture is null ? 0 : 1,
            0,
            0),
        GeneralParameters = new Vector4(
            GetScalar(material, "SAL_HED_EYE_Emis", 0), 0, 0, 0),
        EyeParameters2 = new Vector4(0, 0, 0, material.IsLe3 ? 1 : 0)
    };

    private static HeadPreviewMaterialConstants CreateTurianSkin(
        HeadPreviewMaterial material,
        bool diagnostic,
        float family,
        HeadPreviewMaterialBindings bindings) => new()
    {
        BaseColor = diagnostic ? new Vector4(DiagnosticColor(material.Family), 1)
            : GetVector(material, "SkinTone", new Vector4(0.45f, 0.28f, 0.18f, 1)),
        SecondaryColor = GetVector(material, "TUR_HED_Diff_02_Colour", Vector4.One),
        TertiaryColor = GetVector(material, "TUR_HED_Addn_Colour", Vector4.One),
        QuaternaryColor = GetVector(material, "TUR_HED_Tatt_Colour", Vector4.Zero),
        SpecularColor = GetVector(material, "TUR_HED_Spec_Colour", new Vector4(0.2f, 0.2f, 0.2f, 1)),
        ScatterColor = GetVector(material, "SkinLightScattering", new Vector4(1, 0.3f, 0.019f, 1)),
        FreckleRedColor = GetVector(material, "TUR_HED_Addn_Mask_Vector", new Vector4(0, 1, 0, 1)),
        FreckleGreenColor = GetVector(material, "TUR_HED_Addn_Spec_Colour", Vector4.Zero),
        FreckleBlueColor = GetVector(material, "TUR_HED_Tatt_Spec_Colour", Vector4.Zero),
        BlondeColor = GetVector(material, "TUR_HED_Diff_Tint_Bone", Vector4.One),
        TransmissionColor = Vector4.Zero,
        EyeEmissiveColor = GetVector(material, "TUR_HED_Diff_Tint_Socket", Vector4.One),
        SurfaceParameters = new Vector4(0.42f, diagnostic ? 1 : 0, family, 0),
        TextureFlags0 = new Vector4(bindings.Diffuse is null ? 0 : 1, bindings.Normal is null ? 0 : 1,
            bindings.Mask is null ? 0 : 1, bindings.Detail is null ? 0 : 1),
        TextureFlags1 = new Vector4(bindings.Auxiliary1 is null ? 0 : 1,
            bindings.Auxiliary2 is null ? 0 : 1, 0, 0),
        GeneralParameters = new Vector4(
            GetScalar(material, "TUR_HED_TMis_Multiplier", 1),
            GetScalar(material, "TUR_HED_Diffuse02_Scalar", 0),
            GetScalar(material, "TUR_HED_Spwr_Skin_Scalar", 1),
            GetScalar(material, "TUR_HED_Spwr_Bone_Scalar", 1)),
        SkinParameters0 = new Vector4(
            GetScalar(material, "TUR_HED_Tatt_01_Scalar", 0),
            GetScalar(material, "TUR_HED_Tatt_02_Scalar", 0),
            GetScalar(material, "Mask", 1), 0),
        SkinParameters1 = GetVector(material, "TUR_HED_Tatt_01_Vector", Vector4.Zero),
        SkinParameters2 = GetVector(material, "TUR_HED_Tatt_02_Vector", Vector4.Zero),
        SkinParameters3 = GetVector(material, "TUR_HED_Tatt_01", Vector4.Zero),
        ScalpParameters0 = GetVector(material, "TUR_HED_Tatt_02", Vector4.Zero),
        ScalpParameters1 = GetVector(material, "TUR_HED_Diff_Tint_Teeth", Vector4.One),
        EyeParameters2 = new Vector4(0, 0, 0, material.IsLe3 ? 1 : 0)
    };

    private static HeadPreviewMaterialConstants CreateTurianEyes(
        HeadPreviewMaterial material,
        bool diagnostic,
        float family,
        HeadPreviewMaterialBindings bindings) => new()
    {
        BaseColor = diagnostic ? new Vector4(DiagnosticColor(material.Family), 1)
            : GetVector(material, "EYE_Tint", Vector4.One),
        SecondaryColor = GetVector(material, "TUR_EYE_Iris_Spec_Colour_Vector", Vector4.One),
        TertiaryColor = GetVector(material, "TUR_EYE_Lens_Spec_Colour_Vector", Vector4.One),
        QuaternaryColor = GetVector(material, "TUR_EYE_White_Spec_Colour_Vector", Vector4.One),
        SurfaceParameters = new Vector4(0.14f, diagnostic ? 1 : 0, family, 0),
        TextureFlags0 = new Vector4(bindings.Diffuse is null ? 0 : 1, bindings.Normal is null ? 0 : 1,
            bindings.Mask is null ? 0 : 1, bindings.Detail is null ? 0 : 1),
        TextureFlags1 = material.IsLe3
            ? new Vector4(
                material.FixedCubeTexture is null ? 0 : 1,
                material.SecondaryFixedCubeTexture is null ? 0 : 1,
                0, 0)
            : new Vector4(bindings.Auxiliary1 is null ? 0 : 1, 0, 0, 0),
        GeneralParameters = material.IsLe3
            ? new Vector4(
                GetScalar(material, "CubeMap_Intensity", 0),
                GetScalar(material, "Eye_Specular", 1),
                GetScalar(material, "EYE_Spec_Power", 300),
                0)
            : new Vector4(
                GetScalar(material, "Eye_Pupil", 1),
                GetScalar(material, "TUR_EYE_Iris_SPwr_Scalar", 1),
                GetScalar(material, "TUR_EYE_Lens_SPwr_Scalar", 300),
                GetScalar(material, "TUR_EYE_White_SPwr_Scalar", 60)),
        EyeParameters2 = new Vector4(0, 0, 0, material.IsLe3 ? 1 : 0)
    };

    private static HeadPreviewMaterialConstants CreateKroganSkin(
        HeadPreviewMaterial material,
        bool diagnostic,
        float family,
        HeadPreviewMaterialBindings bindings) => new()
    {
        BaseColor = diagnostic ? new Vector4(DiagnosticColor(material.Family), 1)
            : GetVector(material, "SkinTone", new Vector4(0.7817508f, 0.5113979f, 0.19751646f, 1)),
        SecondaryColor = GetVector(material, "KRO_HED_Helmet_Tint", new Vector4(0.9489651f, 0.45907995f, 0.325037f, 1)),
        TertiaryColor = GetVector(material, "KRO_HED_Face_Grad_Vector", new Vector4(0.5295233f, 0.37361506f, 0.19397219f, 1)),
        QuaternaryColor = GetVector(material, "KRO_HED_Shell_Grad_Vector", new Vector4(0.1636407f, 0.013473397f, 0.0016869153f, 1)),
        SpecularColor = GetVector(material, "KRO_HED_Spec_Add", new Vector4(0.2468f, 0.176774f, 0.108711f, 0)),
        ScatterColor = GetVector(material, "SkinLightScattering", new Vector4(0.45f, 0.15f, 0.03f, 1)),
        FreckleRedColor = GetVector(material, "KRO_HED_Mask_Vector", new Vector4(0, 0, 0, 1)),
        FreckleGreenColor = GetVector(material, "KRO_HED_Addn_Colour_Vector", new Vector4(0.13902247f, 0.065753885f, 0.039947174f, 1)),
        FreckleBlueColor = GetVector(material, "KRO_HED_Lips_Grad_Vector", new Vector4(0.103634104f, 0.09387588f, 0.06772459f, 1)),
        BlondeColor = GetVector(material, "KRO_HED_Teeth_Vector", new Vector4(0.6320427f, 0.5668098f, 0.42590535f, 1)),
        TransmissionColor = GetVector(material, "KRO_HED_Shell_Spec_Add", new Vector4(0.014311f, 0.012664f, 0.007155f, 1)),
        SurfaceParameters = new Vector4(0.42f, diagnostic ? 1 : 0, family, material.IsLe2 ? 1 : 0),
        TextureFlags0 = new Vector4(bindings.Diffuse is null ? 0 : 1, bindings.Normal is null ? 0 : 1,
            bindings.Mask is null ? 0 : 1, bindings.Detail is null ? 0 : 1),
        TextureFlags1 = new Vector4(bindings.Auxiliary1 is null ? 0 : 1,
            bindings.Auxiliary2 is null ? 0 : 1, 0, 0),
        GeneralParameters = new Vector4(
            GetScalar(material, "KRO_HED_Face_Grad_Scalar", 0),
            GetScalar(material, "KRO_HED_Shell_Grad_Scalar", 1),
            GetScalar(material, "KRO_HED_Lips_Grad_Scalar", 0.95600003f),
            GetScalar(material, "KRO_HED_Addn_Colour_Scalar", 0.995000064f)),
        SkinParameters0 = new Vector4(
            GetScalar(material, "KRO_HED_Spec_Scalar", 1),
            GetScalar(material, "Wrex_Spec_Scalar", 0),
            GetScalar(material, "KRO_HED_SPwr_Scalar", 1),
            GetScalar(material, "KRO_HED_Tmis_Scalar", 0.55f)),
        EyeParameters2 = new Vector4(0, 0, 0, material.IsLe3 ? 1 : 0)
    };

    private static HeadPreviewMaterialConstants CreateBatarianSkin(
        HeadPreviewMaterial material,
        bool diagnostic,
        float family,
        HeadPreviewMaterialBindings bindings) => new()
    {
        BaseColor = diagnostic ? new Vector4(DiagnosticColor(material.Family), 1)
            : GetVector(material, "SkinTone", new Vector4(0.42f, 0.22f, 0.1f, 1)),
        SecondaryColor = GetVector(material, "BAT_HED_Teeth_Vector", new Vector4(0.58f, 0.48f, 0.34f, 1)),
        TertiaryColor = GetVector(material, "BAT_HED_Neck_Grad_Vector", Vector4.One),
        QuaternaryColor = GetVector(material, "BAT_HED_Face_Grad_Vector", Vector4.One),
        FreckleRedColor = GetVector(material, "BAT_HED_TopHead_Grad_Vector", Vector4.One),
        FreckleGreenColor = GetVector(material, "BAT_HED_Addn_Colour_Vector", Vector4.One),
        FreckleBlueColor = GetVector(material, "BAT_HED_Mask_Vector", new Vector4(0, 1, 0, 1)),
        SpecularColor = GetVector(material, "BAT_HED_Addn_Spec_Vector", Vector4.Zero),
        ScatterColor = GetVector(material, "SkinLightScattering", new Vector4(0.45f, 0.15f, 0.03f, 1)),
        // The audited LE3 colour, specular, scattering, and transmission
        // chains are instruction-equivalent to LE2. LE3 selects its separate
        // stored-RGB normal path through EyeParameters2.w below.
        SurfaceParameters = new Vector4(
            0.42f,
            diagnostic ? 1 : 0,
            family,
            material.IsLe2 || material.IsLe3 ? 1 : 0),
        TextureFlags0 = new Vector4(bindings.Diffuse is null ? 0 : 1, bindings.Normal is null ? 0 : 1,
            bindings.Mask is null ? 0 : 1, bindings.Detail is null ? 0 : 1),
        TextureFlags1 = new Vector4(bindings.Auxiliary1 is null ? 0 : 1,
            bindings.Auxiliary2 is null ? 0 : 1, 0, 0),
        GeneralParameters = new Vector4(
            GetScalar(material, "BAT_HED_Neck_Grad_Scalar", 0),
            GetScalar(material, "BAT_HED_Face_Grad_Scalar", 0),
            GetScalar(material, "BAT_HED_TopHead_Grad_Scalar", 0),
            GetScalar(material, "BAT_HED_Addn_Diffuse_Blend_Scalar", 0)),
        SkinParameters0 = new Vector4(
            GetScalar(material, "Blowout_Scalar", 1),
            GetScalar(material, "BAT_HED_Addn_Colour_Scalar", 0),
            GetScalar(material, "BAT_HED_SPwr_Scalar", 0.6f),
            GetScalar(material, "BAT_HED_Tmis_Scalar", 0)),
        EyeParameters2 = new Vector4(0, 0, 0, material.IsLe3 ? 1 : 0)
    };

    private static HeadPreviewMaterialConstants CreateKroganEyes(
        HeadPreviewMaterial material,
        bool diagnostic,
        float family,
        HeadPreviewMaterialBindings bindings) => new()
    {
        BaseColor = diagnostic ? new Vector4(DiagnosticColor(material.Family), 1)
            : GetVector(material, "EYE_Tint", new Vector4(0.010397803f, 0.007155037f, 0.016988052f, 1)),
        SpecularColor = Vector4.One,
        SurfaceParameters = new Vector4(0.14f, diagnostic ? 1 : 0, family, material.IsLe2 ? 1 : 0),
        TextureFlags0 = new Vector4(bindings.Diffuse is null ? 0 : 1, bindings.Normal is null ? 0 : 1,
            bindings.Mask is null ? 0 : 1, bindings.Detail is null ? 0 : 1),
        TextureFlags1 = new Vector4(bindings.Auxiliary1 is null ? 0 : 1,
            material.FixedCubeTexture is null ? 0 : 1, 0, 0),
        GeneralParameters = new Vector4(GetScalar(material, "Krogan_Pupil", 2), 0, 0, 0),
        EyeParameters2 = new Vector4(0, 0, 0, material.IsLe3 ? 1 : 0)
    };

    private static HeadPreviewMaterialConstants CreateVorchaSkin(
        HeadPreviewMaterial material,
        bool diagnostic,
        float family,
        HeadPreviewMaterialBindings bindings) => new()
    {
        BaseColor = diagnostic ? new Vector4(DiagnosticColor(material.Family), 1)
            : GetVector(material, "SkinTone", Vector4.One),
        SecondaryColor = GetVector(material, "ALN_HED_Diff_Tint_Muzzle2", Vector4.One),
        TertiaryColor = GetVector(material, "ALN_HED_Diff_Tint_Teeth", Vector4.One),
        QuaternaryColor = GetVector(material, "ALN_HED_Diff_Tint_Muzzle", Vector4.One),
        SpecularColor = GetVector(material, "ALN_HED_Spec_Colour", new Vector4(0.2f, 0.2f, 0.2f, 1)),
        ScatterColor = GetVector(material, "SkinLightScattering", new Vector4(0.45f, 0.15f, 0.03f, 1)),
        FreckleRedColor = GetVector(material, "Tattoo_Chooser", new Vector4(1, 0, 0, 0)),
        FreckleGreenColor = GetVector(material, "Tattoo_Color", Vector4.One),
        TransmissionColor = GetVector(material, "Tmissive", Vector4.Zero),
        SurfaceParameters = new Vector4(0.42f, diagnostic ? 1 : 0, family, 0),
        TextureFlags0 = new Vector4(bindings.Diffuse is null ? 0 : 1, bindings.Normal is null ? 0 : 1,
            bindings.Mask is null ? 0 : 1, bindings.Detail is null ? 0 : 1),
        GeneralParameters = new Vector4(
            GetScalar(material, "ALN_HED_Spwr_Skin_Scalar", 0.2f),
            GetScalar(material, "ALN_HED_Spwr_Muzzle_Scalar", 0.5f), 0, 0),
        EyeParameters2 = new Vector4(0, 0, 0, material.IsLe3 ? 1 : 0)
    };

    private static HeadPreviewMaterialConstants CreateVorchaEyes(
        HeadPreviewMaterial material,
        bool diagnostic,
        float family,
        HeadPreviewMaterialBindings bindings) => new()
    {
        BaseColor = diagnostic ? new Vector4(DiagnosticColor(material.Family), 1)
            : GetVector(material, "EYE_Tint_Iris", Vector4.One),
        SecondaryColor = GetVector(material, "EYE_Glow", Vector4.Zero),
        SpecularColor = Vector4.One,
        SurfaceParameters = new Vector4(0.12f, diagnostic ? 1 : 0, family, 0),
        TextureFlags0 = new Vector4(bindings.Diffuse is null ? 0 : 1, bindings.Normal is null ? 0 : 1, 0, 0),
        GeneralParameters = new Vector4(
            GetScalar(material, "EYE_Spec", 2),
            GetScalar(material, "EYE_Spec_Power", 4),
            GetScalar(material, "EYE_Glow_Intensity", 1.5f), 0),
        EyeParameters2 = new Vector4(0, 0, 0, material.IsLe3 ? 1 : 0)
    };

    private static float FamilyIndex(HeadMaterialFamily family) => family switch
    {
        HeadMaterialFamily.Skin => 1,
        HeadMaterialFamily.AsariSkin => 6,
        HeadMaterialFamily.SalarianSkin => 7,
        HeadMaterialFamily.SalarianEyes => 8,
        HeadMaterialFamily.TurianSkin => 9,
        HeadMaterialFamily.TurianEyes => 10,
        HeadMaterialFamily.KroganSkin => 11,
        HeadMaterialFamily.KroganEyes => 12,
        HeadMaterialFamily.BatarianSkin => 13,
        HeadMaterialFamily.Scalp => 2,
        HeadMaterialFamily.Eyes => 3,
        HeadMaterialFamily.Lashes => 4,
        HeadMaterialFamily.Hair => 5,
        HeadMaterialFamily.MaskedHair => 14,
        HeadMaterialFamily.VorchaSkin => 15,
        HeadMaterialFamily.VorchaEyes => 16,
        _ => 0
    };

    private static float DefaultRoughness(HeadMaterialFamily family) => family switch
    {
        HeadMaterialFamily.Eyes or HeadMaterialFamily.SalarianEyes or HeadMaterialFamily.TurianEyes or HeadMaterialFamily.KroganEyes or HeadMaterialFamily.VorchaEyes => 0.18f,
        HeadMaterialFamily.Teeth => 0.28f,
        HeadMaterialFamily.Lashes => 0.8f,
        HeadMaterialFamily.Hair or HeadMaterialFamily.MaskedHair => 0.65f,
        _ => 0.48f
    };

    private static Vector4 ResolveBaseColor(HeadPreviewMaterial material) => material.Family switch
    {
        HeadMaterialFamily.Skin or HeadMaterialFamily.AsariSkin or HeadMaterialFamily.SalarianSkin or HeadMaterialFamily.TurianSkin or HeadMaterialFamily.Scalp => GetVector(material, "SkinTone", new Vector4(0.58f, 0.34f, 0.26f, 1)),
        HeadMaterialFamily.Eyes => GetVector(material, "EYE_Iris_Colour_Vector", new Vector4(0.12f, 0.27f, 0.31f, 1)),
        HeadMaterialFamily.Lashes => GetVector(material, "HED_Lash_Diff_Vector", new Vector4(0.025f, 0.02f, 0.018f, 1)),
        HeadMaterialFamily.Hair => GetVector(material, "HED_Hair_Colour_Vector", new Vector4(0.07f, 0.045f, 0.03f, 1)),
        _ => new Vector4(0.42f, 0.45f, 0.5f, 1)
    };

    private static Vector4 ResolveSecondaryColor(HeadPreviewMaterial material) => material.Family switch
    {
        HeadMaterialFamily.Eyes => GetVector(material, "EYE_White_Colour_Vector", Vector4.One),
        HeadMaterialFamily.Skin => GetVector(material, "HED_Addn_Colour_Vector", Vector4.Zero),
        HeadMaterialFamily.Scalp => GetVector(material, "HED_Hair_Colour_Vector", Vector4.Zero),
        HeadMaterialFamily.Hair when material.IsLe3 => GetVector(material, "Highlight1Color", Vector4.One),
        _ => Vector4.One
    };

    private static Vector4 ResolveTertiaryColor(HeadPreviewMaterial material) => material.Family switch
    {
        HeadMaterialFamily.Skin => GetVector(material, "HED_Mask_Vector", Vector4.Zero),
        HeadMaterialFamily.Scalp => GetVector(material, "HED_Teeth_Vector", Vector4.One),
        HeadMaterialFamily.Eyes => GetVector(material, "EyeLightScattering", Vector4.Zero),
        HeadMaterialFamily.Hair when material.IsLe3 => GetVector(material, "Highlight2Color", Vector4.One),
        _ => Vector4.Zero
    };

    private static Vector4 ResolveQuaternaryColor(HeadPreviewMaterial material) => material.Family switch
    {
        HeadMaterialFamily.Skin => GetVector(material, "HED_Scar_Colour_Vector", Vector4.Zero),
        HeadMaterialFamily.Scalp => GetVector(material, "HED_TClr_Vector", Vector4.Zero),
        HeadMaterialFamily.Eyes => GetVector(material, "HED_EYE_FX_Vector", Vector4.Zero),
        _ => Vector4.Zero
    };

    private static Vector4 GetVector(HeadPreviewMaterial material, string name, Vector4 fallback)
    {
        if (!material.Supports(name, MaterialParameterKind.Vector) ||
            !material.Vectors.TryGetValue(name, out var value))
        {
            return fallback;
        }
        return new Vector4(value.X, value.Y, value.Z, value.W);
    }

    private static float GetScalar(HeadPreviewMaterial material, string name, float fallback) =>
        material.Supports(name, MaterialParameterKind.Scalar) && material.Scalars.TryGetValue(name, out var value)
            ? value
            : fallback;

    private static Vector3 DiagnosticColor(HeadMaterialFamily family) => family switch
    {
        HeadMaterialFamily.Skin => new Vector3(0.9f, 0.23f, 0.18f),
        HeadMaterialFamily.AsariSkin => new Vector3(0.4f, 0.21f, 0.73f),
        HeadMaterialFamily.SalarianSkin => new Vector3(0.12f, 0.74f, 0.31f),
        HeadMaterialFamily.SalarianEyes => new Vector3(0.37f, 0.95f, 0.66f),
        HeadMaterialFamily.TurianSkin => new Vector3(0.19f, 0.45f, 0.66f),
        HeadMaterialFamily.TurianEyes => new Vector3(0.35f, 0.72f, 0.95f),
        HeadMaterialFamily.KroganSkin => new Vector3(0.47f, 0.078f, 0),
        HeadMaterialFamily.KroganEyes => new Vector3(0.78f, 0.31f, 0.04f),
        HeadMaterialFamily.BatarianSkin => new Vector3(0.631f, 0.435f, 0.063f),
        HeadMaterialFamily.Scalp => new Vector3(0.95f, 0.58f, 0.12f),
        HeadMaterialFamily.Eyes => new Vector3(0.12f, 0.78f, 0.96f),
        HeadMaterialFamily.Teeth => new Vector3(0.92f, 0.92f, 0.7f),
        HeadMaterialFamily.Lashes => new Vector3(0.72f, 0.25f, 0.9f),
        HeadMaterialFamily.Hair => new Vector3(0.2f, 0.75f, 0.35f),
        HeadMaterialFamily.MaskedHair => new Vector3(0.1f, 0.9f, 0.55f),
        HeadMaterialFamily.Accessory => new Vector3(0.22f, 0.45f, 0.95f),
        _ => new Vector3(0.65f, 0.68f, 0.72f)
    };
}
