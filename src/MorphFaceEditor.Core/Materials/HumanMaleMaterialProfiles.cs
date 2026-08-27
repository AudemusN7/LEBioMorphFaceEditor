namespace MorphFaceEditor.Core.Materials;

public static class HumanMaterialProfiles
{
    private static readonly IReadOnlyDictionary<string, HeadMaterialFamily> MasterFamilies =
        new Dictionary<string, HeadMaterialFamily>(StringComparer.OrdinalIgnoreCase)
        {
            ["HMN_HED_LASH_Unlit_MASTER_MAT"] = HeadMaterialFamily.Lashes,
            ["HMM_EYE_MASTER_OVRD_MAT"] = HeadMaterialFamily.Eyes,
            ["HMF_EYE_MASTER_OVRD_MAT"] = HeadMaterialFamily.Eyes,
            ["HMM_HED_PRONPC_MASTER_FACE_MAT"] = HeadMaterialFamily.Skin,
            ["HMM_HED_PROCustom_MASTER_FACE_MAT"] = HeadMaterialFamily.Skin,
            ["HMF_HED_PRO_MASTER_FACE_MAT"] = HeadMaterialFamily.Skin,
            ["HMF_HED_PROCustom_MASTER_FACE_MAT"] = HeadMaterialFamily.Skin,
            ["ASA_HED_MASTER_FACE_MAT_1a"] = HeadMaterialFamily.AsariSkin,
            ["SAL_HED_PRO_MASTER_MAT"] = HeadMaterialFamily.SalarianSkin,
            ["SAL_HED_EYE_MASTER_MAT"] = HeadMaterialFamily.SalarianEyes,
            ["TUR_HED_PRO_MASTER_MAT"] = HeadMaterialFamily.TurianSkin,
            ["TUR_HED_EYE_MASTER_MAT"] = HeadMaterialFamily.TurianEyes,
            ["BAT_HED_PRO_MASTER_MAT"] = HeadMaterialFamily.BatarianSkin,
            ["KRO_HED_PRO_MASTER_MAT"] = HeadMaterialFamily.KroganSkin,
            ["KRO_HED_EYE_MASTER_MAT"] = HeadMaterialFamily.KroganEyes,
            ["HMM_HED_PRO_MASTER_SCALP_MAT"] = HeadMaterialFamily.Scalp,
            ["HMN_HED_PRO_MASTER_HAIR_MAT"] = HeadMaterialFamily.Hair,
            ["HMN_HED_PRO_MASTER_ADDN_HAIR_MAT"] = HeadMaterialFamily.Hair
        };

    public static IReadOnlyList<MaterialParameterDefinition> Definitions { get; } = CreateDefinitions();

    public static HeadMaterialFamily ClassifyMaster(string? masterMaterialName) =>
        masterMaterialName is not null && MasterFamilies.TryGetValue(masterMaterialName, out var family)
            ? family
            : HeadMaterialFamily.Unknown;

    public static HeadMaterialBlendMode BlendMode(HeadMaterialFamily family) => family switch
    {
        HeadMaterialFamily.Scalp => HeadMaterialBlendMode.Masked,
        HeadMaterialFamily.Lashes or HeadMaterialFamily.Hair => HeadMaterialBlendMode.Translucent,
        _ => HeadMaterialBlendMode.Opaque
    };

    public static bool IsTwoSided(HeadMaterialFamily family) =>
        family is HeadMaterialFamily.Lashes or HeadMaterialFamily.Hair;

    public static MaterialParameterDefinition Describe(
        string name,
        MaterialParameterKind kind,
        HeadMaterialFamily fallbackFamily = HeadMaterialFamily.Unknown)
    {
        var match = Definitions.FirstOrDefault(definition =>
            definition.Kind == kind && string.Equals(definition.Name, name, StringComparison.OrdinalIgnoreCase));
        return match ?? new MaterialParameterDefinition(
            name,
            Humanize(name),
            "Other",
            kind,
            fallbackFamily,
            kind == MaterialParameterKind.Scalar ? -4 : 0,
            kind == MaterialParameterKind.Scalar ? 4 : 1,
            0.01f,
            Description: "Parameter found in the BioMaterialOverride graph but not in the recovered LE1 human schema.");
    }

    public static TextureRole InferTextureRole(string parameterName) =>
        Describe(parameterName, MaterialParameterKind.Texture).TextureRole;

    private static IReadOnlyList<MaterialParameterDefinition> CreateDefinitions()
    {
        var result = new List<MaterialParameterDefinition>();
        AddScalars(result, HeadMaterialFamily.Lashes, "Lashes", 0, 1,
            "HED_Lash_Opac_Scalar");
        AddScalars(result, HeadMaterialFamily.Lashes, "Lashes", 0, 4,
            "HED_Lash_Spec_Scalar");
        AddTexture(result, HeadMaterialFamily.Lashes, "HED_Lash_Diff", "Lash opacity", "Lashes", TextureRole.Diffuse, TextureColorSpace.Srgb, TextureAlphaPolicy.Translucency);
        AddVector(result, HeadMaterialFamily.Lashes, "HED_Lash_Diff_Vector", "Lash colour", "Lashes");

        AddScalars(result, HeadMaterialFamily.Eyes, "Eyes", -1, 1,
            "V_Offset", "U_Offset", "HED_EYE_FX_Scalar");
        AddScalar(result, HeadMaterialFamily.Eyes, "X_Tile", "Eyes", 0.1f, 4, 0.01f,
            "LE2 eye diffuse/normal horizontal tiling.");
        AddScalar(result, HeadMaterialFamily.Eyes, "Y_Tile", "Eyes", 0.1f, 4, 0.01f,
            "LE2 eye diffuse/normal vertical tiling.");
        AddScalar(result, HeadMaterialFamily.Eyes, "Iris_Colour_Multiplier", "Eyes", 0, 4, 0.01f,
            "LE2 multiplier applied to the authored iris colour.");
        AddScalar(result, HeadMaterialFamily.Eyes, "Primary_Reflection_Multiplier", "Eyes", 0, 4, 0.01f,
            "LE2 primary corneal/environment reflection strength.");
        AddScalar(result, HeadMaterialFamily.Eyes, "Secondary_Reflection_Multiplier", "Eyes", 0, 4, 0.01f,
            "LE2 secondary broad eye reflection strength.");
        AddScalar(result, HeadMaterialFamily.Eyes, "Sclera_Darken", "Eyes", 0, 1, 0.005f,
            "LE2 sclera contribution/darkening control recovered from the compiled shader.");
        AddScalar(result, HeadMaterialFamily.Eyes, "Emis_Scalar", "Eyes", 0, 10, 0.05f,
            "LE2 eye emissive strength. Vanilla morph faces leave this at zero, but custom faces may author it.");
        AddTexture(result, HeadMaterialFamily.Eyes, "EYE_Diff", "Eye diffuse", "Eyes", TextureRole.Diffuse, TextureColorSpace.Srgb);
        AddTexture(result, HeadMaterialFamily.Eyes, "EYE_Iris_Norm", "Iris normal", "Eyes", TextureRole.Normal, TextureColorSpace.Linear);
        AddTexture(result, HeadMaterialFamily.Eyes, "EYE_Lens_Norm", "Lens normal", "Eyes", TextureRole.Normal, TextureColorSpace.Linear);
        AddTexture(result, HeadMaterialFamily.Eyes, "EYE_Mask", "Eye mask / specular", "Eyes", TextureRole.Mask, TextureColorSpace.Linear);
        AddVector(result, HeadMaterialFamily.Eyes, "EYE_Iris_Colour_Vector", "Iris colour", "Eyes");
        AddVector(result, HeadMaterialFamily.Eyes, "EYE_White_Colour_Vector", "Eye white colour", "Eyes");
        AddVector(result, HeadMaterialFamily.Eyes, "EyeLightScattering", "Eye light scattering", "Eyes");
        AddVector(result, HeadMaterialFamily.Eyes, "Tmission_Color", "Eye transmission colour", "Eyes");
        AddVector(result, HeadMaterialFamily.Eyes, "HED_EYE_FX_Vector", "Eye FX colour", "Eyes");
        AddVector(result, HeadMaterialFamily.Eyes, "Emis_Color", "Emissive colour", "Eyes");

        AddScalars(result, HeadMaterialFamily.Skin, "Face", 0, 1,
            "HED_Mask_Scalar", "HED_Frek_RedChannel_Scalar",
            "HED_Frek_BlueChannel_Scalar", "HED_Frek_GreenChannel_Scalar", "HED_Norm_Blend",
            "HED_TMis_Scalar", "HED_Scar_Scalar", "HED_Brow_FadeOut_Scalar", "HED_Addn_Blend_Scalar");
        AddScalars(result, HeadMaterialFamily.Skin, "Face", 0, 4,
            "HED_Addn_Blowout_Scalar", "HED_Addn_Colour_02_Scalar", "HED_Addn_SPwr_Add_Scalar",
            "HED_Addn_Spec_Add_Scalar", "HED_Addn_Add_Scalar", "HED_Addn_Multiply_Scalar");
        AddScalar(result, HeadMaterialFamily.Skin, "HED_SPwr_Scalar", "Face", 0.1f, 32, 0.05f,
            "Direct LE1 face specular exponent. HED_Diff alpha supplies the spatial specular mask.");
        AddTexture(result, HeadMaterialFamily.Skin, "HED_Diff", "Face diffuse", "Face", TextureRole.Diffuse, TextureColorSpace.Srgb);
        AddTexture(result, HeadMaterialFamily.Skin, "HED_Mask", "Face mask", "Face", TextureRole.Mask, TextureColorSpace.Linear);
        AddTexture(result, HeadMaterialFamily.Skin, "HED_Frek", "Freckles", "Face", TextureRole.Detail, TextureColorSpace.Linear);
        AddTexture(result, HeadMaterialFamily.Skin, "HED_Norm_02", "Secondary normal", "Face", TextureRole.Normal, TextureColorSpace.Linear);
        AddTexture(result, HeadMaterialFamily.Skin, "HED_Norm", "Face normal", "Face", TextureRole.Normal, TextureColorSpace.Linear);
        // RGB stores a tangent-space addition normal and alpha stores the facial-hair/addition mask.
        AddTexture(result, HeadMaterialFamily.Skin, "HED_Addn", "Face addition", "Face", TextureRole.Detail, TextureColorSpace.Linear, TextureAlphaPolicy.Mask);
        AddVectors(result, HeadMaterialFamily.Skin, "Face",
            "SkinTone", "HED_TClr_Vector", "HED_Scar_Colour_Vector", "HED_Addn_Colour_Vector",
            "HED_Spec_Add_Vector", "HED_Frek_RedChannel_Vector", "HED_Mask_Vector", "SkinLightScattering",
            "HED_Frek_BlueChannel_Vector", "HED_Frek_GreenChannel_Vector", "blonde");

        // HMF replaces the HMM packed face/scar selector with a dedicated
        // makeup mask. The base and direct-light FXC permutations prove the
        // tint, lip, addition, and specular parameter bindings. The stock mask
        // places the nominal Brow branch over the eyeshadow region, while the
        // nominal EyeShadow branch supplies the broader eye/mouth makeup colour.
        AddScalars(result, HeadMaterialFamily.Skin, "Female face", 0, 1,
            "HED_Blush_Scalar", "HED_Brow_Tint_Scalar", "HED_EyeShadow_Tint_Scalar",
            "HED_Lips_Tint_Scalar", "HED_Spec_NoBrow", "HED_Addn_Spec_Lips_Scalar");
        AddScalar(result, HeadMaterialFamily.Skin, "HED_Addn_SPwr_Lips_Scalar", "Female face", 0.1f, 32, 0.05f,
            "HMF lip-region Phong exponent recovered from the direct-light permutation.");
        AddTexture(result, HeadMaterialFamily.Skin, "HED_Makeup_Mask", "Makeup mask", "Female face",
            TextureRole.Other, TextureColorSpace.Linear, TextureAlphaPolicy.Ignore,
            "HMF packed makeup selector: red selects the eyeshadow tint branch, green selects the broader eye/mouth makeup branch, and blue/alpha selects the lip region.");
        AddVectors(result, HeadMaterialFamily.Skin, "Female face",
            "HED_Blush_Vector", "HED_Brow_Tint_Vector", "HED_EyeShadow_Tint_Vector", "HED_Lips_Tint_Vector");

        AddScalars(result, HeadMaterialFamily.AsariSkin, "Asari face", 0, 1,
            "ASA_HED_Addn_Mask_Scalar", "ASA_HED_MakeUp_Switch_Scalar",
            "ASA_HED_Diffuse_02_Colour_Scalar", "ASA_HED_Addn_Colour_Scalar",
            "ASA_HED_Tatt_01_Scalar", "ASA_HED_Tatt_02_Scalar", "ASA_HED_Tatt_Blender_Scalar",
            "ASA_HED_Face_Fresnel_Scalar", "ASA_HED_Lip_Gloss_Scalar", "Mask", "ASA_HED_TMis_Switch");
        AddScalar(result, HeadMaterialFamily.AsariSkin, "ASA_HED_SPwr_Add_Scalar", "Asari face", 0, 10, 0.05f,
            "Adds to the Asari skin specular exponent.");
        AddScalar(result, HeadMaterialFamily.AsariSkin, "ASA_HED_SPwr_Multiplier_Scalar", "Asari face", 0, 6, 0.03f,
            "Multiplies the packed Asari skin specular power.");
        AddTexture(result, HeadMaterialFamily.AsariSkin, "ASA_HED_Diff", "Asari diffuse", "Asari face",
            TextureRole.Diffuse, TextureColorSpace.Srgb, TextureAlphaPolicy.Ignore);
        AddTexture(result, HeadMaterialFamily.AsariSkin, "ASA_HED_Norm", "Asari normal", "Asari face",
            TextureRole.Normal, TextureColorSpace.Linear);
        AddTexture(result, HeadMaterialFamily.AsariSkin, "ASA_HED_Mask", "Asari packed mask", "Asari face",
            TextureRole.Mask, TextureColorSpace.Linear);
        AddTexture(result, HeadMaterialFamily.AsariSkin, "ASA_HED_Addn", "Complexion addition", "Asari face",
            TextureRole.Detail, TextureColorSpace.Linear, TextureAlphaPolicy.Mask);
        AddTexture(result, HeadMaterialFamily.AsariSkin, "ASA_HED_MakeUp", "Asari makeup mask", "Asari face",
            TextureRole.Other, TextureColorSpace.Linear);
        AddTexture(result, HeadMaterialFamily.AsariSkin, "ASA_HED_Tatt", "Asari tattoo texture", "Asari face",
            TextureRole.Other, TextureColorSpace.Linear);
        AddVectors(result, HeadMaterialFamily.AsariSkin, "Asari face",
            "ASA_HED_Addn_Mask_Vector", "SkinTone", "ASA_HED_Teeth_Colour_Vector",
            "ASA_HED_MakeUp_Eyes", "ASA_HED_MakeUp_Lips", "ASA_HED_Makeup_Blender_Vector",
            "ASA_HED_Diffuse_02_Colour", "ASA_HED_Addn_Colour", "ASA_HED_Tatt_Colour",
            "ASA_HED_Tatt_01_Vector", "ASA_HED_Tatt_01", "ASA_HED_Tatt_02",
            "ASA_HED_Tatt_02_Vector", "ASA_HED_Spec_Add", "ASA_HED_TClr_Tint", "SkinLightScattering");

        AddScalars(result, HeadMaterialFamily.SalarianSkin, "Salarian face", 0, 1,
            "SAL_HED_Addn_Mask_Scalar", "SAL_HED_Addn_Blend_Scalar",
            "SAL_HED_Diffuse02_Scalar", "SAL_HED_Tatt_01_Scalar",
            "SAL_HED_Tatt_02_Scalar", "SAL_HED_Addn_Spec_Scalar", "SAL_HED_Spec_Scalar");
        AddTexture(result, HeadMaterialFamily.SalarianSkin, "SAL_HED_Diff", "Salarian diffuse", "Salarian face",
            TextureRole.Diffuse, TextureColorSpace.Srgb, TextureAlphaPolicy.Ignore);
        AddTexture(result, HeadMaterialFamily.SalarianSkin, "SAL_HED_Norm", "Salarian normal", "Salarian face",
            TextureRole.Normal, TextureColorSpace.Linear);
        AddTexture(result, HeadMaterialFamily.SalarianSkin, "SAL_HED_Mask", "Salarian packed mask", "Salarian face",
            TextureRole.Mask, TextureColorSpace.Linear);
        AddTexture(result, HeadMaterialFamily.SalarianSkin, "SAL_HED_Addn", "Salarian complexion addition", "Salarian face",
            TextureRole.Detail, TextureColorSpace.Linear, TextureAlphaPolicy.Mask);
        AddTexture(result, HeadMaterialFamily.SalarianSkin, "SAL_HED_Tint", "Salarian surface tint mask", "Salarian face",
            TextureRole.Mask, TextureColorSpace.Linear);
        AddTexture(result, HeadMaterialFamily.SalarianSkin, "SAL_HED_Tatt", "Salarian tattoo selector", "Salarian face",
            TextureRole.Mask, TextureColorSpace.Linear);
        AddTexture(result, HeadMaterialFamily.SalarianSkin, "SAL_HED_SpecMap", "Salarian specular map", "Salarian face",
            TextureRole.Specular, TextureColorSpace.Linear);
        AddVectors(result, HeadMaterialFamily.SalarianSkin, "Salarian face",
            "SAL_HED_Addn_Mask_Vector", "SkinTone", "SAL_HED_Addn_Colour",
            "SAL_HED_Diff_02_Colour", "SAL_HED_Tatt_Colour", "SAL_HED_Tatt_01_Vector",
            "SAL_HED_Tatt_01", "SAL_HED_Tatt_02", "SAL_HED_Tatt_02_Vector",
            "SAL_HED_Addn_Spec_Colour", "SAL_HED_Spec_Colour", "SAL_HED_Tmis_COLOUR",
            "SkinLightScattering");

        AddScalar(result, HeadMaterialFamily.SalarianEyes, "SAL_HED_EYE_Emis", "Salarian eyes", 0, 1, 0.01f,
            "Emissive intensity selected by the red channel of the Salarian eye specular map.");
        AddTexture(result, HeadMaterialFamily.SalarianEyes, "SAL_HED_EYE_Diff", "Salarian eye diffuse", "Salarian eyes",
            TextureRole.Diffuse, TextureColorSpace.Srgb);
        AddTexture(result, HeadMaterialFamily.SalarianEyes, "SAL_HED_EYE_Norm", "Salarian eye normal", "Salarian eyes",
            TextureRole.Normal, TextureColorSpace.Linear);
        AddTexture(result, HeadMaterialFamily.SalarianEyes, "SAL_HED_EYE_Spec", "Salarian eye mask and specular map", "Salarian eyes",
            TextureRole.Specular, TextureColorSpace.Linear);
        AddVectors(result, HeadMaterialFamily.SalarianEyes, "Salarian eyes",
            "SAL_HED_EYE_Iris_Vector", "SAL_HED_EYE_Pupil_Vector");

        AddScalars(result, HeadMaterialFamily.TurianSkin, "Turian face", 0, 1,
            "TUR_HED_Diffuse02_Scalar", "TUR_HED_Tatt_01_Scalar", "TUR_HED_Tatt_02_Scalar",
            "TUR_HED_Spwr_Skin_Scalar", "TUR_HED_Spwr_Bone_Scalar", "TUR_HED_TMis_Multiplier");
        AddScalar(result, HeadMaterialFamily.TurianSkin, "Mask", "Turian face", 0, 1, 0.01f,
            "Compiled teeth-region opacity control: values below 0.33 hide pixels selected by TUR_HED_Tint green.");
        AddTexture(result, HeadMaterialFamily.TurianSkin, "TUR_HED_Diff", "Turian diffuse", "Turian face",
            TextureRole.Diffuse, TextureColorSpace.Srgb, TextureAlphaPolicy.Ignore,
            "Turian base diffuse; alpha participates in the compiled surface response.");
        AddTexture(result, HeadMaterialFamily.TurianSkin, "TUR_HED_Norm", "Turian normal", "Turian face",
            TextureRole.Normal, TextureColorSpace.Linear);
        AddTexture(result, HeadMaterialFamily.TurianSkin, "TUR_HED_Mask", "Turian face-region mask", "Turian face",
            TextureRole.Mask, TextureColorSpace.Linear);
        AddTexture(result, HeadMaterialFamily.TurianSkin, "TUR_HED_Tint", "Turian surface tint mask", "Turian face",
            TextureRole.Mask, TextureColorSpace.Linear);
        AddTexture(result, HeadMaterialFamily.TurianSkin, "TUR_HED_Addn", "Turian complexion addition", "Turian face",
            TextureRole.Detail, TextureColorSpace.Linear, TextureAlphaPolicy.Mask);
        AddTexture(result, HeadMaterialFamily.TurianSkin, "TUR_HED_Tatt", "Turian tattoo selector", "Turian face",
            TextureRole.Mask, TextureColorSpace.Linear);
        AddVectors(result, HeadMaterialFamily.TurianSkin, "Turian face",
            "TUR_HED_Addn_Mask_Vector", "TUR_HED_Addn_Colour", "TUR_HED_Diff_Tint_Bone",
            "TUR_HED_Diff_Tint_Socket", "TUR_HED_Diff_Tint_Teeth", "SkinTone",
            "TUR_HED_Diff_02_Colour", "TUR_HED_Tatt_Colour", "TUR_HED_Tatt_01_Vector",
            "TUR_HED_Tatt_01", "TUR_HED_Tatt_02", "TUR_HED_Tatt_02_Vector",
            "TUR_HED_Spec_Colour", "TUR_HED_Addn_Spec_Colour", "TUR_HED_Tatt_Spec_Colour",
            "SkinLightScattering");

        AddScalars(result, HeadMaterialFamily.TurianEyes, "Turian eyes", 0, 1, "Eye_Pupil");
        AddScalar(result, HeadMaterialFamily.TurianEyes, "TUR_EYE_Iris_SPwr_Scalar", "Turian eyes", 0.1f, 500, 1,
            "Iris specular exponent.");
        AddScalar(result, HeadMaterialFamily.TurianEyes, "TUR_EYE_Lens_SPwr_Scalar", "Turian eyes", 0.1f, 500, 1,
            "Lens specular exponent.");
        AddScalar(result, HeadMaterialFamily.TurianEyes, "TUR_EYE_White_SPwr_Scalar", "Turian eyes", 0.1f, 500, 1,
            "Sclera specular exponent.");
        AddTexture(result, HeadMaterialFamily.TurianEyes, "TUR_EYE_Diff", "Turian eye diffuse", "Turian eyes",
            TextureRole.Diffuse, TextureColorSpace.Srgb);
        AddTexture(result, HeadMaterialFamily.TurianEyes, "TUR_EYE_Mask", "Turian sclera and iris mask", "Turian eyes",
            TextureRole.Mask, TextureColorSpace.Linear);
        AddTexture(result, HeadMaterialFamily.TurianEyes, "TUR_Eye_Spec", "Turian eye specular", "Turian eyes",
            TextureRole.Specular, TextureColorSpace.Linear);
        AddTexture(result, HeadMaterialFamily.TurianEyes, "TUR_EYE_Iris_Norm", "Turian iris normal", "Turian eyes",
            TextureRole.Normal, TextureColorSpace.Linear);
        AddTexture(result, HeadMaterialFamily.TurianEyes, "TUR_EYE_Lens_Norm", "Turian lens normal", "Turian eyes",
            TextureRole.Normal, TextureColorSpace.Linear);
        AddVectors(result, HeadMaterialFamily.TurianEyes, "Turian eyes",
            "EYE_Tint", "TUR_EYE_Lens_Spec_Colour_Vector", "TUR_EYE_Iris_Spec_Colour_Vector",
            "TUR_EYE_White_Spec_Colour_Vector");
        AddScalar(result, HeadMaterialFamily.TurianEyes, "CubeMap_Intensity", "Turian eyes", 0, 8, 0.02f,
            "LE3 parameter-cube reflection intensity.");
        AddScalar(result, HeadMaterialFamily.TurianEyes, "Eye_Specular", "Turian eyes", 0, 4, 0.01f,
            "LE3 direct-light specular strength.");
        AddScalar(result, HeadMaterialFamily.TurianEyes, "EYE_Spec_Power", "Turian eyes", 0.1f, 500, 1,
            "LE3 reflection-vector Phong exponent.");
        AddTexture(result, HeadMaterialFamily.TurianEyes, "EYE_Diff", "LE3 Turian eye diffuse", "Turian eyes",
            TextureRole.Diffuse, TextureColorSpace.Srgb);
        AddTexture(result, HeadMaterialFamily.TurianEyes, "Eye_Norm", "LE3 Turian eye normal", "Turian eyes",
            TextureRole.Normal, TextureColorSpace.Linear);

        AddScalar(result, HeadMaterialFamily.BatarianSkin, "Blowout_Scalar", "Batarian face", 0, 8, 0.04f,
            "Diffuse gain. LE2 clamps this value to one before applying it to gradient layers.");
        AddScalars(result, HeadMaterialFamily.BatarianSkin, "Batarian face", 0, 1,
            "BAT_HED_Neck_Grad_Scalar", "BAT_HED_Face_Grad_Scalar", "BAT_HED_TopHead_Grad_Scalar",
            "BAT_HED_Addn_Diffuse_Blend_Scalar", "BAT_HED_Addn_Colour_Scalar", "BAT_HED_Tmis_Scalar");
        AddScalar(result, HeadMaterialFamily.BatarianSkin, "BAT_HED_SPwr_Scalar", "Batarian face", 0, 1, 0.005f,
            "LE1 reflection-vector Phong exponent multiplier. This parameter is compiled out of the LE2 master.");
        AddTexture(result, HeadMaterialFamily.BatarianSkin, "BAT_HED_Diff", "Batarian diffuse", "Batarian face",
            TextureRole.Diffuse, TextureColorSpace.Srgb);
        AddTexture(result, HeadMaterialFamily.BatarianSkin, "BAT_HED_Norm", "Batarian normal", "Batarian face",
            TextureRole.Normal, TextureColorSpace.Linear);
        AddTexture(result, HeadMaterialFamily.BatarianSkin, "BAT_HED_Mask", "Batarian complexion region mask", "Batarian face",
            TextureRole.Mask, TextureColorSpace.Linear,
            description: "RGB is dotted with BAT_HED_Mask_Vector RGB to select the complexion region.");
        AddTexture(result, HeadMaterialFamily.BatarianSkin, "BAT_HED_Addn", "Batarian complexion texture", "Batarian face",
            TextureRole.Detail, TextureColorSpace.Linear, TextureAlphaPolicy.Mask,
            "Red/green perturb the normal and alpha gates the complexion colour and specular additions.");
        AddTexture(result, HeadMaterialFamily.BatarianSkin, "BAT_HED_Spec", "Batarian specular and teeth selector", "Batarian face",
            TextureRole.Specular, TextureColorSpace.Linear,
            description: "Red controls specular power, green masks skin scattering, and blue selects the teeth colour.");
        AddTexture(result, HeadMaterialFamily.BatarianSkin, "BAT_HED_Tint", "Batarian gradient selector", "Batarian face",
            TextureRole.Mask, TextureColorSpace.Linear,
            description: "Green, red, and blue respectively select the neck, face, and top-head gradients.");
        AddVectors(result, HeadMaterialFamily.BatarianSkin, "Batarian face",
            "BAT_HED_Mask_Vector", "SkinTone", "BAT_HED_Teeth_Vector",
            "BAT_HED_Neck_Grad_Vector", "BAT_HED_Face_Grad_Vector", "BAT_HED_TopHead_Grad_Vector",
            "BAT_HED_Addn_Colour_Vector", "BAT_HED_Addn_Spec_Vector", "SkinLightScattering");

        AddScalars(result, HeadMaterialFamily.KroganSkin, "Krogan face", 0, 1,
            "KRO_HED_Face_Grad_Scalar", "KRO_HED_Shell_Grad_Scalar",
            "KRO_HED_Lips_Grad_Scalar", "KRO_HED_Addn_Colour_Scalar");
        AddScalar(result, HeadMaterialFamily.KroganSkin, "KRO_HED_Spec_Scalar", "Krogan face", 0, 1, 0.005f,
            "Multiplies the ordinary skin-specular colour after the compiled complexion mask chain.");
        AddScalar(result, HeadMaterialFamily.KroganSkin, "Wrex_Spec_Scalar", "Krogan face", 0, 1, 0.005f,
            "Blends the complete specular colour toward Diffuse alpha * 1.4 * packed Tint RGB. Intended for Wrex-specific inputs; other faces can expose saturated selector colours.");
        AddScalar(result, HeadMaterialFamily.KroganSkin, "KRO_HED_SPwr_Scalar", "Krogan face", 0, 1, 0.005f,
            "Reflection-vector Phong exponent: Tint alpha * this value * 100.");
        AddScalar(result, HeadMaterialFamily.KroganSkin, "KRO_HED_Tmis_Scalar", "Krogan face", 0, 1, 0.005f,
            "Transmission term used by the compiled wrapped-light equation.");
        AddTexture(result, HeadMaterialFamily.KroganSkin, "KRO_HED_Diff", "Krogan diffuse", "Krogan face",
            TextureRole.Diffuse, TextureColorSpace.Srgb, TextureAlphaPolicy.Mask,
            "RGB is surface colour; alpha participates in the compiled Krogan specular and transmission equations.");
        AddTexture(result, HeadMaterialFamily.KroganSkin, "KRO_HED_Norm", "Krogan normal", "Krogan face",
            TextureRole.Normal, TextureColorSpace.Linear);
        AddTexture(result, HeadMaterialFamily.KroganSkin, "KRO_HED_Mask", "Krogan region mask", "Krogan face",
            TextureRole.Mask, TextureColorSpace.Linear,
            description: "RGB is dotted with KRO_HED_Mask_Vector RGB to select the complexion region.");
        AddTexture(result, HeadMaterialFamily.KroganSkin, "KRO_HED_Addn", "Krogan complexion normal", "Krogan face",
            TextureRole.Detail, TextureColorSpace.Linear, TextureAlphaPolicy.Mask,
            "Red/green perturb the normal and alpha gates the complexion colour and specular layers.");
        AddTexture(result, HeadMaterialFamily.KroganSkin, "KRO_HED_Tint", "Krogan surface selector", "Krogan face",
            TextureRole.Mask, TextureColorSpace.Linear,
            description: "Red selects helmet/skin tint, blue selects teeth, green and alpha drive compiled lighting terms.");
        AddTexture(result, HeadMaterialFamily.KroganSkin, "KRO_HED_Tnt2", "Krogan gradient selector", "Krogan face",
            TextureRole.Mask, TextureColorSpace.Linear,
            description: "Green, red, and blue respectively select the face, head-plate, and lip gradients.");
        AddVectors(result, HeadMaterialFamily.KroganSkin, "Krogan face",
            "KRO_HED_Mask_Vector", "SkinTone", "KRO_HED_Helmet_Tint",
            "KRO_HED_Face_Grad_Vector", "KRO_HED_Shell_Grad_Vector",
            "KRO_HED_Lips_Grad_Vector", "KRO_HED_Addn_Colour_Vector",
            "KRO_HED_Teeth_Vector", "KRO_HED_Shell_Spec_Add", "KRO_HED_Spec_Add",
            "SkinLightScattering");

        AddScalar(result, HeadMaterialFamily.KroganEyes, "Krogan_Pupil", "Krogan eyes", 0, 4, 0.01f,
            "Horizontal pupil scale applied around UV centre, selected by KRO_Eye_Mask green.");
        AddTexture(result, HeadMaterialFamily.KroganEyes, "KRO_EYE_Diff", "Krogan eye diffuse", "Krogan eyes",
            TextureRole.Diffuse, TextureColorSpace.Srgb);
        AddTexture(result, HeadMaterialFamily.KroganEyes, "KRO_Eye_Mask", "Krogan eye mask", "Krogan eyes",
            TextureRole.Mask, TextureColorSpace.Linear);
        AddTexture(result, HeadMaterialFamily.KroganEyes, "KRO_Eye_Spec", "Krogan eye specular", "Krogan eyes",
            TextureRole.Specular, TextureColorSpace.Linear);
        AddTexture(result, HeadMaterialFamily.KroganEyes, "KRO_EYE_Iris_Norm", "Krogan iris normal", "Krogan eyes",
            TextureRole.Normal, TextureColorSpace.Linear);
        AddTexture(result, HeadMaterialFamily.KroganEyes, "KRO_EYE_Lens_Norm", "Krogan lens normal", "Krogan eyes",
            TextureRole.Normal, TextureColorSpace.Linear);
        AddVector(result, HeadMaterialFamily.KroganEyes, "EYE_Tint", "Eye tint", "Krogan eyes");

        AddScalars(result, HeadMaterialFamily.Scalp, "Scalp / mouth", 0, 1,
            "HED_Scalp_Mask_OverlayKill_Scalar", "HAIR_Mask_Alpha_Scalar",
            "Mask", "HED_Teeth_Scalar", "HED_Scalp_Mask_Scalar",
            "HED_Scalp_BuzzCut_Alpha_Scalar", "HAIR_Shine_Desaturate_Scalar");
        AddScalar(result, HeadMaterialFamily.Scalp, "HED_SPwr_Scalar", "Scalp / mouth", 0.1f, 32, 0.05f,
            "Direct LE1 scalp Phong exponent.");
        AddScalar(result, HeadMaterialFamily.Scalp, "HED_Scalp_PhongSpec_Scalar", "Scalp / mouth", 0, 8, 0.02f,
            "Strength of the scalp Phong/specular response.");
        AddScalar(result, HeadMaterialFamily.Scalp, "HED_Spec_Aniso_Exp_Scalar", "Scalp / mouth", 0.1f, 8, 0.02f,
            "Shapes the normalized anisotropic hair colour; it is not the tangent-lobe exponent.");
        AddTexture(result, HeadMaterialFamily.Scalp, "HED_Scalp_SpecShift", "Primary specular shift", "Scalp / mouth", TextureRole.Specular, TextureColorSpace.Linear);
        AddTexture(result, HeadMaterialFamily.Scalp, "HED_Teeth_Diff", "Teeth selector", "Scalp / mouth", TextureRole.Mask, TextureColorSpace.Linear,
            TextureAlphaPolicy.Ignore, "Packed LE1 selector: red controls masked coverage and green isolates the teeth layer.");
        AddTexture(result, HeadMaterialFamily.Scalp, "HED_Scalp_SpecShift2", "Secondary specular shift", "Scalp / mouth", TextureRole.Specular, TextureColorSpace.Linear);
        AddTexture(result, HeadMaterialFamily.Scalp, "HED_Scalp_Diff", "Scalp diffuse", "Scalp / mouth", TextureRole.Diffuse, TextureColorSpace.Srgb, TextureAlphaPolicy.Mask);
        AddTexture(result, HeadMaterialFamily.Scalp, "HED_Scalp_Spec", "Scalp specular", "Scalp / mouth", TextureRole.Specular, TextureColorSpace.Linear);
        AddTexture(result, HeadMaterialFamily.Scalp, "HED_Scalp_Norm", "Scalp normal", "Scalp / mouth", TextureRole.Normal, TextureColorSpace.Linear);
        AddTexture(result, HeadMaterialFamily.Scalp, "HED_Tang", "Tangent map", "Scalp / mouth", TextureRole.Tangent, TextureColorSpace.Linear);
        AddVectors(result, HeadMaterialFamily.Scalp, "Scalp / mouth",
            "HED_Teeth_Vector", "HED_TClr_Vector", "HED_Hair_Colour_Vector", "SkinLightScattering", "HED_Spec_Add_Vector", "SkinTone");

        AddScalar(result, HeadMaterialFamily.Hair, "Highlight2SpecExp_Scalar", "Hair", 0, 500, 2.5f,
            "Declared by the LE1 master graph but optimized out of the supplied base/direct-light FXC permutations.");
        AddScalar(result, HeadMaterialFamily.Hair, "Highlight1SpecExp_Scalar", "Hair", 0, 500, 2.5f,
            "Declared by the LE1 master graph but optimized out of the supplied base/direct-light FXC permutations.");
        AddTexture(result, HeadMaterialFamily.Hair, "HAIR_Diff", "Hair diffuse / opacity", "Hair", TextureRole.Diffuse, TextureColorSpace.Srgb, TextureAlphaPolicy.Translucency);
        AddTexture(result, HeadMaterialFamily.Hair, "HAIR_ADDN_Diff", "Additional hair diffuse / opacity", "Hair", TextureRole.Diffuse, TextureColorSpace.Srgb, TextureAlphaPolicy.Translucency,
            "LE3 HMF additional-hair packed map: red selects the two highlight lobes, green supplies base colour, and alpha supplies opacity.");
        AddScalars(result, HeadMaterialFamily.Hair, "Hair", 0, 4,
            "Highlight1Intensity", "Highlight2Intensity", "HAIR_Spec_Contribution_Scalar");
        AddScalar(result, HeadMaterialFamily.Hair, "Hair_Spec_Aniso_Exp_Scalar", "Hair", 0.1f, 8, 0.02f,
            "Authored HMF hair override retained for material round-tripping; optimized out of the shared LE1 hair master permutation.");
        AddTexture(result, HeadMaterialFamily.Hair, "HAIR_Mask", "Hair mask", "Hair", TextureRole.Mask, TextureColorSpace.Linear);
        AddTexture(result, HeadMaterialFamily.Hair, "HAIR_Norm", "Hair normal", "Hair", TextureRole.Normal, TextureColorSpace.Linear);
        AddTexture(result, HeadMaterialFamily.Hair, "HAIR_SpecShift", "Primary hair specular shift", "Hair", TextureRole.Specular, TextureColorSpace.Linear);
        AddTexture(result, HeadMaterialFamily.Hair, "HAIR_SpecShift2", "Secondary hair specular shift", "Hair", TextureRole.Specular, TextureColorSpace.Linear);
        AddTexture(result, HeadMaterialFamily.Hair, "HAIR_Tang", "Hair tangent map", "Hair", TextureRole.Tangent, TextureColorSpace.Linear);
        AddVector(result, HeadMaterialFamily.Hair, "HED_Hair_Colour_Vector", "Hair colour", "Hair");
        return result;
    }

    private static void AddScalars(List<MaterialParameterDefinition> values, HeadMaterialFamily family, string group, float minimum, float maximum, params string[] names)
    {
        foreach (var name in names)
        {
            values.Add(new MaterialParameterDefinition(name, Humanize(name), group, MaterialParameterKind.Scalar, family, minimum, maximum, Math.Max((maximum - minimum) / 200f, 0.001f)));
        }
    }

    private static void AddScalar(
        List<MaterialParameterDefinition> values,
        HeadMaterialFamily family,
        string name,
        string group,
        float minimum,
        float maximum,
        float step,
        string description) =>
        values.Add(new MaterialParameterDefinition(
            name, Humanize(name), group, MaterialParameterKind.Scalar, family,
            minimum, maximum, step, Description: description));

    private static void AddVector(List<MaterialParameterDefinition> values, HeadMaterialFamily family, string name, string label, string group) =>
        values.Add(new MaterialParameterDefinition(name, label, group, MaterialParameterKind.Vector, family));

    private static void AddVectors(List<MaterialParameterDefinition> values, HeadMaterialFamily family, string group, params string[] names)
    {
        foreach (var name in names)
        {
            AddVector(values, family, name, Humanize(name), group);
        }
    }

    private static void AddTexture(
        List<MaterialParameterDefinition> values,
        HeadMaterialFamily family,
        string name,
        string label,
        string group,
        TextureRole role,
        TextureColorSpace colorSpace,
        TextureAlphaPolicy alphaPolicy = TextureAlphaPolicy.Ignore,
        string description = "") =>
        values.Add(new MaterialParameterDefinition(name, label, group, MaterialParameterKind.Texture, family,
            TextureRole: role, ColorSpace: colorSpace, AlphaPolicy: alphaPolicy, Description: description));

    private static string Humanize(string name) => string.Join(' ', name
        .Replace('_', ' ')
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(word => word.Length <= 4 && word.All(char.IsUpper) ? word : char.ToUpperInvariant(word[0]) + word[1..]));
}
