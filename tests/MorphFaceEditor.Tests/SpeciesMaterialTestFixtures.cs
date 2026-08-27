using System.Numerics;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Rendering;

namespace MorphFaceEditor.Tests;

using static MorphFaceEditor.Tests.MaterialTestFixtures;

/// <summary>Constructs alien-species preview materials from explicit reverse-engineered inputs.</summary>
public static class SpeciesMaterialTestFixtures
{
    internal static HeadPreviewMaterial CreateAsariMaterial(
        string key,
        byte[] tattooPattern,
        Vector4 tattooSelector,
        float tattooBlend,
        byte[]? addition = null,
        float additionMaskScalar = 0,
        byte[]? skinNoise = null,
        float complexionStrength = 0,
        byte[]? makeup = null,
        float makeupStrength = 0,
        Vector4? makeupEyes = null,
        Vector4? makeupLips = null,
        Vector4? makeupBlender = null,
        bool isLe3 = false)
    {
        var textures = new Dictionary<string, HeadPreviewTexture>(StringComparer.OrdinalIgnoreCase);
        Add("ASA_HED_Diff", [128, 128, 128, 255], TextureRole.Diffuse);
        Add("ASA_HED_Norm", [128, 128, 255, 255], TextureRole.Normal);
        Add("ASA_HED_Mask", [0, 0, 0, 255], TextureRole.Mask);
        Add("ASA_HED_Addn", addition ?? [128, 128, 255, 0], TextureRole.Detail);
        Add("ASA_HED_MakeUp", makeup ?? [0, 0, 0, 0], TextureRole.Other);
        Add("ASA_HED_Tatt", tattooPattern, TextureRole.Other);
        Add("__ASA_SkinNoise", skinNoise ?? [255, 255, 255, 255], TextureRole.Detail);
        Add("__ASA_SpecMultiplierMask", [0, 0, 0, 255], TextureRole.Specular);

        return new HeadPreviewMaterial(
            key,
            key,
            HeadMaterialFamily.AsariSkin,
            HeadMaterialBlendMode.Opaque,
            false,
            new Dictionary<string, float>
            {
                ["ASA_HED_Addn_Mask_Scalar"] = additionMaskScalar,
                ["ASA_HED_Addn_Colour_Scalar"] = complexionStrength,
                ["ASA_HED_MakeUp_Switch_Scalar"] = makeupStrength,
                ["ASA_HED_Diffuse_02_Colour_Scalar"] = 0,
                ["ASA_HED_Tatt_01_Scalar"] = 1,
                ["ASA_HED_Tatt_02_Scalar"] = 0,
                ["ASA_HED_Tatt_Blender_Scalar"] = tattooBlend,
                ["ASA_HED_Lip_Gloss_Scalar"] = 0,
                ["ASA_HED_SPwr_Add_Scalar"] = 0,
                ["ASA_HED_SPwr_Multiplier_Scalar"] = 8,
                ["Mask"] = 1
            },
            new Dictionary<string, Vector4>
            {
                ["SkinTone"] = new Vector4(0.25f, 0.45f, 0.8f, 1),
                ["ASA_HED_Teeth_Colour_Vector"] = Vector4.One,
                ["ASA_HED_Addn_Mask_Vector"] = Vector4.Zero,
                ["ASA_HED_Addn_Colour"] = new Vector4(0.9f, 0.12f, 0.15f, 1),
                ["ASA_HED_MakeUp_Eyes"] = makeupEyes ?? Vector4.Zero,
                ["ASA_HED_MakeUp_Lips"] = makeupLips ?? Vector4.Zero,
                ["ASA_HED_Makeup_Blender_Vector"] = makeupBlender ?? Vector4.Zero,
                ["ASA_HED_Diffuse_02_Colour"] = Vector4.Zero,
                ["ASA_HED_Tatt_Colour"] = new Vector4(0.12f, 0.7f, 0.9f, 1),
                ["ASA_HED_Tatt_01_Vector"] = Vector4.Zero,
                ["ASA_HED_Tatt_01"] = tattooSelector,
                ["ASA_HED_Tatt_02"] = Vector4.Zero,
                ["ASA_HED_Tatt_02_Vector"] = Vector4.Zero,
                ["ASA_HED_Spec_Add"] = Vector4.Zero,
                ["SkinLightScattering"] = Vector4.Zero
            },
            textures)
        {
            IsLe3 = isLe3
        };

        void Add(string name, byte[] rgba, TextureRole role) =>
            textures[name] = CreateTexture(name, rgba, role);
    }

    internal static HeadPreviewMaterial CreateSalarianMaterial(
        string key,
        byte[]? addition = null,
        float additionBlend = 0,
        byte[]? tattooPattern = null,
        Vector4? tattooSelector = null,
        byte[]? specularMap = null,
        Vector4? complexionMask = null,
        byte[]? normal = null,
        byte[]? tint = null,
        Vector4? scattering = null,
        bool isLe3 = false)
    {
        var textures = new Dictionary<string, HeadPreviewTexture>(StringComparer.OrdinalIgnoreCase);
        Add("SAL_HED_Diff", [128, 128, 128, 255], TextureRole.Diffuse);
        Add("SAL_HED_Norm", normal ?? [128, 128, 255, 255], TextureRole.Normal);
        Add("SAL_HED_Mask", [255, 0, 0, 0], TextureRole.Mask);
        Add("SAL_HED_Addn", addition ?? [128, 128, 0, 0], TextureRole.Detail);
        Add("SAL_HED_Tint", tint ?? [0, 255, 255, 255], TextureRole.Mask);
        if (tattooPattern is not null) Add("SAL_HED_Tatt", tattooPattern, TextureRole.Mask);
        if (specularMap is not null) Add("SAL_HED_SpecMap", specularMap, TextureRole.Specular);

        return new HeadPreviewMaterial(
            key,
            key,
            HeadMaterialFamily.SalarianSkin,
            HeadMaterialBlendMode.Opaque,
            false,
            new Dictionary<string, float>
            {
                ["SAL_HED_Addn_Mask_Scalar"] = 0,
                ["SAL_HED_Addn_Blend_Scalar"] = additionBlend,
                ["SAL_HED_Diffuse02_Scalar"] = 0,
                ["SAL_HED_Tatt_01_Scalar"] = 0,
                ["SAL_HED_Tatt_02_Scalar"] = 0,
                ["SAL_HED_Addn_Spec_Scalar"] = 0,
                ["SAL_HED_Spec_Scalar"] = 0.02f
            },
            new Dictionary<string, Vector4>
            {
                ["SkinTone"] = new Vector4(0.24f, 0.55f, 0.34f, 1),
                ["SAL_HED_Addn_Mask_Vector"] = complexionMask ?? new Vector4(1, 0, 0, 0),
                ["SAL_HED_Addn_Colour"] = new Vector4(0.9f, 0.08f, 0.08f, 1),
                ["SAL_HED_Diff_02_Colour"] = Vector4.Zero,
                ["SAL_HED_Tatt_Colour"] = new Vector4(0.05f, 0.85f, 0.95f, 1),
                ["SAL_HED_Tatt_01_Vector"] = new Vector4(1, 0, 0, 0),
                ["SAL_HED_Tatt_01"] = tattooSelector ?? Vector4.Zero,
                ["SAL_HED_Tatt_02"] = Vector4.Zero,
                ["SAL_HED_Tatt_02_Vector"] = Vector4.Zero,
                ["SAL_HED_Addn_Spec_Colour"] = Vector4.Zero,
                ["SAL_HED_Spec_Colour"] = specularMap is null ? Vector4.Zero : new Vector4(4, 4, 4, 1),
                ["SAL_HED_Tmis_COLOUR"] = Vector4.Zero,
                ["SkinLightScattering"] = scattering ?? Vector4.Zero
            },
            textures)
        {
            IsLe3 = isLe3
        };

        void Add(string name, byte[] rgba, TextureRole role) =>
            textures[name] = CreateTexture(name, rgba, role);
    }

    internal static HeadPreviewMaterial CreateSalarianEyeMaterial(
        string key,
        byte[] specRgba,
        float emissive = 0,
        byte? chromeValue = null,
        byte? visorValue = null)
    {
        var diffuse = CreateTexture("SAL_HED_EYE_Diff", [128, 128, 128, 255], TextureRole.Diffuse);
        var normal = CreateTexture("SAL_HED_EYE_Norm", [128, 128, 255, 255], TextureRole.Normal);
        var specular = CreateTexture("SAL_HED_EYE_Spec", specRgba, TextureRole.Specular);
        var material = new HeadPreviewMaterial(
            key,
            key,
            HeadMaterialFamily.SalarianEyes,
            HeadMaterialBlendMode.Opaque,
            false,
            new Dictionary<string, float> { ["SAL_HED_EYE_Emis"] = emissive },
            new Dictionary<string, Vector4>
            {
                ["SAL_HED_EYE_Iris_Vector"] = new Vector4(0.08f, 0.75f, 0.2f, 1),
                ["SAL_HED_EYE_Pupil_Vector"] = new Vector4(0.8f, 0.04f, 0.1f, 1)
            },
            new Dictionary<string, HeadPreviewTexture>
            {
                [diffuse.ParameterName] = diffuse,
                [normal.ParameterName] = normal,
                [specular.ParameterName] = specular
            });

        return material with
        {
            FixedCubeTexture = CreateCube("Cube_Chrome", chromeValue),
            SecondaryFixedCubeTexture = CreateCube("Cube_Visor", visorValue)
        };

        static HeadPreviewCubeTexture? CreateCube(string name, byte? value)
        {
            if (value is null)
            {
                return null;
            }

            var face = new byte[] { value.Value, value.Value, value.Value, 255 };
            return new HeadPreviewCubeTexture(
                $"{name}:{value.Value}",
                1,
                Enumerable.Range(0, 6).Select(_ => face.ToArray()).ToArray(),
                TextureColorSpace.Srgb);
        }
    }

    internal static HeadPreviewMaterial CreateTurianMaterial(
        string key,
        byte[] tint,
        Vector4 specular,
        byte[]? addition = null,
        Vector4? additionSpecular = null,
        float transmission = 0,
        byte[]? normal = null,
        bool isLe3 = false,
        byte[]? diffuse = null,
        Vector4? boneTint = null,
        Vector4? tattooColour = null,
        bool fullTattoo = false,
        float maskScalar = 1)
    {
        var textures = new Dictionary<string, HeadPreviewTexture>(StringComparer.OrdinalIgnoreCase);
        Add("TUR_HED_Diff", diffuse ?? [128, 128, 128, 255], TextureRole.Diffuse);
        Add("TUR_HED_Norm", normal ?? [128, 128, 255, 255], TextureRole.Normal);
        Add("TUR_HED_Mask", [255, 0, 0, 0], TextureRole.Mask);
        Add("TUR_HED_Addn", addition ?? [128, 128, 0, 0], TextureRole.Detail);
        Add("TUR_HED_Tint", tint, TextureRole.Mask);
        Add("TUR_HED_Tatt", fullTattoo ? [255, 0, 0, 255] : [0, 0, 0, 255], TextureRole.Mask);

        return new HeadPreviewMaterial(
            key,
            key,
            HeadMaterialFamily.TurianSkin,
            HeadMaterialBlendMode.Opaque,
            false,
            new Dictionary<string, float>
            {
                ["TUR_HED_TMis_Multiplier"] = transmission,
                ["TUR_HED_Diffuse02_Scalar"] = 0,
                ["TUR_HED_Spwr_Skin_Scalar"] = 0.15f,
                ["TUR_HED_Spwr_Bone_Scalar"] = 0.3f,
                ["TUR_HED_Tatt_01_Scalar"] = 0,
                ["TUR_HED_Tatt_02_Scalar"] = 0,
                ["Mask"] = maskScalar
            },
            new Dictionary<string, Vector4>
            {
                ["SkinTone"] = new Vector4(0.22f, 0.45f, 0.34f, 1),
                ["TUR_HED_Diff_02_Colour"] = Vector4.Zero,
                ["TUR_HED_Addn_Colour"] = new Vector4(0.55f, 0.2f, 0.12f, 1),
                ["TUR_HED_Tatt_Colour"] = tattooColour ?? Vector4.Zero,
                ["TUR_HED_Spec_Colour"] = specular,
                ["TUR_HED_Addn_Mask_Vector"] = new Vector4(1, 0, 0, 0),
                ["TUR_HED_Addn_Spec_Colour"] = additionSpecular ?? Vector4.Zero,
                ["TUR_HED_Tatt_Spec_Colour"] = Vector4.Zero,
                ["TUR_HED_Diff_Tint_Bone"] = boneTint ?? Vector4.One,
                ["TUR_HED_Diff_Tint_Socket"] = Vector4.One,
                ["TUR_HED_Diff_Tint_Teeth"] = Vector4.One,
                ["TUR_HED_Tatt_01_Vector"] = fullTattoo ? Vector4.UnitX : Vector4.Zero,
                ["TUR_HED_Tatt_02_Vector"] = Vector4.Zero,
                ["TUR_HED_Tatt_01"] = fullTattoo ? Vector4.UnitX : Vector4.Zero,
                ["TUR_HED_Tatt_02"] = Vector4.Zero,
                ["SkinLightScattering"] = Vector4.Zero
            },
            textures)
        {
            IsLe3 = isLe3
        };

        void Add(string name, byte[] rgba, TextureRole role) =>
            textures[name] = CreateTexture(name, rgba, role);
    }

    internal static HeadPreviewMaterial CreateTurianEyeMaterial(string key, byte[] specRgba)
    {
        var textures = new Dictionary<string, HeadPreviewTexture>(StringComparer.OrdinalIgnoreCase);
        Add("TUR_EYE_Diff", [128, 128, 128, 255], TextureRole.Diffuse);
        Add("TUR_EYE_Iris_Norm", [128, 128, 255, 255], TextureRole.Normal);
        Add("TUR_EYE_Mask", [255, 255, 0, 255], TextureRole.Mask);
        Add("TUR_Eye_Spec", specRgba, TextureRole.Specular);
        Add("TUR_EYE_Lens_Norm", [128, 128, 255, 255], TextureRole.Normal);

        return new HeadPreviewMaterial(
            key,
            key,
            HeadMaterialFamily.TurianEyes,
            HeadMaterialBlendMode.Opaque,
            false,
            new Dictionary<string, float>
            {
                ["Eye_Pupil"] = 1,
                ["TUR_EYE_Iris_SPwr_Scalar"] = 8,
                ["TUR_EYE_Lens_SPwr_Scalar"] = 24,
                ["TUR_EYE_White_SPwr_Scalar"] = 48
            },
            new Dictionary<string, Vector4>
            {
                ["EYE_Tint"] = Vector4.One,
                ["TUR_EYE_Iris_Spec_Colour_Vector"] = new Vector4(0.9f, 0.05f, 0.05f, 1),
                ["TUR_EYE_Lens_Spec_Colour_Vector"] = new Vector4(0.05f, 0.9f, 0.05f, 1),
                ["TUR_EYE_White_Spec_Colour_Vector"] = new Vector4(0.05f, 0.05f, 0.9f, 1)
            },
            textures);

        void Add(string name, byte[] rgba, TextureRole role) =>
            textures[name] = CreateTexture(name, rgba, role);
    }

    internal static HeadPreviewMaterial CreateLe3TurianEyeMaterial(
        string key,
        byte chromeValue,
        byte parameterCubeValue,
        float cubeIntensity,
        float specular)
    {
        var diffuse = CreateTexture("EYE_Diff", [64, 96, 128, 255], TextureRole.Diffuse);
        var normal = CreateTexture("Eye_Norm", [128, 128, 255, 255], TextureRole.Normal);
        return new HeadPreviewMaterial(
            key,
            key,
            HeadMaterialFamily.TurianEyes,
            HeadMaterialBlendMode.Opaque,
            false,
            new Dictionary<string, float>
            {
                ["CubeMap_Intensity"] = cubeIntensity,
                ["Eye_Specular"] = specular,
                ["EYE_Spec_Power"] = 24
            },
            new Dictionary<string, Vector4>
            {
                ["EYE_Tint"] = new Vector4(0.2f, 0.35f, 0.15f, 1)
            },
            new Dictionary<string, HeadPreviewTexture>
            {
                [diffuse.ParameterName] = diffuse,
                [normal.ParameterName] = normal
            })
        {
            IsLe3 = true,
            FixedCubeTexture = CreateCube("Cube_Chrome", chromeValue),
            SecondaryFixedCubeTexture = CreateCube("Cube_Eyes", parameterCubeValue)
        };

        static HeadPreviewCubeTexture CreateCube(string name, byte value)
        {
            var face = new byte[] { value, value, value, 255 };
            return new HeadPreviewCubeTexture(
                $"{name}:{value}", 1,
                Enumerable.Range(0, 6).Select(_ => face.ToArray()).ToArray(),
                TextureColorSpace.Srgb);
        }
    }

    internal static HeadPreviewMaterial CreateKroganMaterial(
        string key,
        byte[] tint,
        byte[] gradientSelectors,
        byte[]? addition = null,
        Vector4? complexionMask = null,
        Vector4? complexionColour = null,
        float wrexSpecular = 0,
        float specularPower = 0.2f,
        Vector4? skinSpecular = null,
        byte[]? normal = null,
        bool isLe3 = false)
    {
        var textures = new Dictionary<string, HeadPreviewTexture>(StringComparer.OrdinalIgnoreCase);
        Add("KRO_HED_Diff", [128, 128, 128, 255], TextureRole.Diffuse);
        Add("KRO_HED_Norm", normal ?? [128, 128, 255, 255], TextureRole.Normal);
        Add("KRO_HED_Mask", [255, 0, 0, 255], TextureRole.Mask);
        Add("KRO_HED_Addn", addition ?? [128, 128, 0, 0], TextureRole.Detail);
        Add("KRO_HED_Tint", tint, TextureRole.Mask);
        Add("KRO_HED_Tnt2", gradientSelectors, TextureRole.Mask);

        return new HeadPreviewMaterial(
            key,
            key,
            HeadMaterialFamily.KroganSkin,
            HeadMaterialBlendMode.Opaque,
            false,
            new Dictionary<string, float>
            {
                ["KRO_HED_Face_Grad_Scalar"] = 1,
                ["KRO_HED_Shell_Grad_Scalar"] = 1,
                ["KRO_HED_Lips_Grad_Scalar"] = 1,
                ["KRO_HED_Addn_Colour_Scalar"] = 1,
                ["KRO_HED_Spec_Scalar"] = 1,
                ["Wrex_Spec_Scalar"] = wrexSpecular,
                ["KRO_HED_SPwr_Scalar"] = specularPower,
                ["KRO_HED_Tmis_Scalar"] = 0
            },
            new Dictionary<string, Vector4>
            {
                ["SkinTone"] = new Vector4(0.25f, 0.35f, 0.12f, 1),
                ["KRO_HED_Helmet_Tint"] = new Vector4(0.8f, 0.18f, 0.03f, 1),
                ["KRO_HED_Face_Grad_Vector"] = new Vector4(0.05f, 0.8f, 0.1f, 1),
                ["KRO_HED_Shell_Grad_Vector"] = new Vector4(0.9f, 0.05f, 0.02f, 1),
                ["KRO_HED_Lips_Grad_Vector"] = new Vector4(0.05f, 0.12f, 0.9f, 1),
                ["KRO_HED_Addn_Colour_Vector"] = complexionColour ?? new Vector4(0.95f, 0.04f, 0.6f, 1),
                ["KRO_HED_Mask_Vector"] = complexionMask ?? new Vector4(1, 0, 0, 0),
                ["KRO_HED_Teeth_Vector"] = Vector4.One,
                ["KRO_HED_Spec_Add"] = skinSpecular ?? Vector4.Zero,
                ["KRO_HED_Shell_Spec_Add"] = Vector4.Zero,
                ["SkinLightScattering"] = Vector4.Zero
            },
            textures)
        {
            IsLe3 = isLe3
        };

        void Add(string name, byte[] rgba, TextureRole role) =>
            textures[name] = CreateTexture(name, rgba, role);
    }

    internal static HeadPreviewMaterial CreateBatarianMaterial(
        string key,
        byte[] gradientSelectors,
        byte[]? addition = null,
        Vector4? complexionMask = null,
        Vector4? complexionColour = null,
        byte[]? normal = null,
        bool isLe3 = false)
    {
        var textures = new Dictionary<string, HeadPreviewTexture>(StringComparer.OrdinalIgnoreCase);
        Add("BAT_HED_Diff", [96, 144, 192, 255], TextureRole.Diffuse);
        Add("BAT_HED_Norm", normal ?? [128, 128, 255, 255], TextureRole.Normal);
        Add("BAT_HED_Mask", [255, 0, 0, 255], TextureRole.Mask);
        Add("BAT_HED_Addn", addition ?? [128, 128, 0, 0], TextureRole.Detail);
        Add("BAT_HED_Tint", gradientSelectors, TextureRole.Mask);
        Add("BAT_HED_Spec", [128, 64, 0, 255], TextureRole.Specular);

        return new HeadPreviewMaterial(
            key,
            key,
            HeadMaterialFamily.BatarianSkin,
            HeadMaterialBlendMode.Opaque,
            false,
            new Dictionary<string, float>
            {
                ["Blowout_Scalar"] = 2,
                ["BAT_HED_Neck_Grad_Scalar"] = 1,
                ["BAT_HED_Face_Grad_Scalar"] = 1,
                ["BAT_HED_TopHead_Grad_Scalar"] = 1,
                ["BAT_HED_Addn_Diffuse_Blend_Scalar"] = 0.5f,
                ["BAT_HED_Addn_Colour_Scalar"] = 1,
                ["BAT_HED_SPwr_Scalar"] = 0.2f,
                ["BAT_HED_Tmis_Scalar"] = 0
            },
            new Dictionary<string, Vector4>
            {
                ["SkinTone"] = new Vector4(0.3f, 0.2f, 0.1f, 1),
                ["BAT_HED_Teeth_Vector"] = Vector4.One,
                ["BAT_HED_Neck_Grad_Vector"] = new Vector4(0.05f, 0.8f, 0.1f, 1),
                ["BAT_HED_Face_Grad_Vector"] = new Vector4(0.9f, 0.05f, 0.02f, 1),
                ["BAT_HED_TopHead_Grad_Vector"] = new Vector4(0.05f, 0.12f, 0.9f, 1),
                ["BAT_HED_Addn_Colour_Vector"] = complexionColour ?? new Vector4(0.95f, 0.04f, 0.6f, 1),
                ["BAT_HED_Mask_Vector"] = complexionMask ?? new Vector4(1, 0, 0, 0),
                ["BAT_HED_Addn_Spec_Vector"] = Vector4.Zero,
                ["SkinLightScattering"] = Vector4.Zero
            },
            textures)
        {
            IsLe3 = isLe3
        };

        void Add(string name, byte[] rgba, TextureRole role) =>
            textures[name] = CreateTexture(name, rgba, role);
    }

    internal static HeadPreviewMaterial CreateKroganEyeMaterial(
        string key,
        byte[] specRgba,
        byte cubeValue,
        byte[]? irisNormal = null,
        byte[]? lensNormal = null,
        bool isLe3 = false)
    {
        var textures = new Dictionary<string, HeadPreviewTexture>(StringComparer.OrdinalIgnoreCase);
        Add("KRO_EYE_Diff", [180, 150, 120, 255], TextureRole.Diffuse);
        Add("KRO_EYE_Iris_Norm", irisNormal ?? [128, 128, 255, 255], TextureRole.Normal);
        Add("KRO_Eye_Mask", [255, 255, 0, 255], TextureRole.Mask);
        Add("KRO_Eye_Spec", specRgba, TextureRole.Specular);
        Add("KRO_EYE_Lens_Norm", lensNormal ?? [128, 128, 255, 255], TextureRole.Normal);
        var face = new byte[] { cubeValue, cubeValue, cubeValue, 255 };
        return new HeadPreviewMaterial(
            key,
            key,
            HeadMaterialFamily.KroganEyes,
            HeadMaterialBlendMode.Opaque,
            false,
            new Dictionary<string, float> { ["Krogan_Pupil"] = 2 },
            new Dictionary<string, Vector4> { ["EYE_Tint"] = Vector4.One },
            textures)
        {
            IsLe3 = isLe3,
            FixedCubeTexture = new HeadPreviewCubeTexture(
                $"cube:{cubeValue}", 1,
                Enumerable.Range(0, 6).Select(_ => face.ToArray()).ToArray(),
                TextureColorSpace.Srgb)
        };

        void Add(string name, byte[] rgba, TextureRole role) =>
            textures[name] = CreateTexture(name, rgba, role);
    }
}

