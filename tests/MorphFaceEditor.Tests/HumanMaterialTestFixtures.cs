using System.Numerics;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Rendering;

namespace MorphFaceEditor.Tests;

using static MorphFaceEditor.Tests.MaterialTestFixtures;

/// <summary>Constructs Human, scalp, hair, lash and eye preview materials from explicit test inputs.</summary>
public static class HumanMaterialTestFixtures
{
    internal static HeadPreviewMaterial CreateScalpMaterial(
        string key,
        float scalpMask,
        float mask,
        Vector4? hairColour = null,
        Vector4? specularColour = null)
    {
        var diffuse = CreateTexture("HED_Scalp_Diff", [128, 128, 128, 255], TextureRole.Diffuse, true);
        var scalpSpec = CreateTexture("HED_Scalp_Spec", [255, 255, 255, 255], TextureRole.Specular);
        var teeth = CreateTexture("HED_Teeth_Diff", [255, 0, 0, 255], TextureRole.Diffuse);
        return new HeadPreviewMaterial(
            key, key, HeadMaterialFamily.Scalp, HeadMaterialBlendMode.Masked, false,
            new Dictionary<string, float>
            {
                ["HED_Scalp_Mask_Scalar"] = scalpMask,
                ["Mask"] = mask,
                ["HED_Teeth_Scalar"] = 4,
                ["HED_Scalp_PhongSpec_Scalar"] = 0
            },
            new Dictionary<string, Vector4>
            {
                ["SkinTone"] = new Vector4(0.5f, 0.5f, 0.5f, 1),
                ["HED_Hair_Colour_Vector"] = hairColour ?? new Vector4(0.5f, 0.5f, 0.5f, 1),
                ["HED_Spec_Add_Vector"] = specularColour ?? new Vector4(0.22f, 0.22f, 0.22f, 1)
            },
            new Dictionary<string, HeadPreviewTexture>
            {
                [diffuse.ParameterName] = diffuse,
                [scalpSpec.ParameterName] = scalpSpec,
                [teeth.ParameterName] = teeth
            });
    }

    internal static HeadPreviewMaterial CreateSkinMaterial(string key, byte[] maskRgba)
    {
        var diffuse = CreateTexture("HED_Diff", [96, 72, 56, 255], TextureRole.Diffuse);
        var mask = CreateTexture("HED_Mask", maskRgba, TextureRole.Mask);
        return new HeadPreviewMaterial(
            key, key, HeadMaterialFamily.Skin, HeadMaterialBlendMode.Opaque, false,
            new Dictionary<string, float>
            {
                ["HED_TMis_Scalar"] = 1,
                ["HED_Mask_Scalar"] = 1,
                ["HED_Scar_Scalar"] = 1
            },
            new Dictionary<string, Vector4>
            {
                ["SkinTone"] = new Vector4(0.5f, 0.5f, 0.5f, 1),
                ["HED_Mask_Vector"] = Vector4.One,
                ["HED_Scar_Colour_Vector"] = Vector4.One,
                ["HED_Spec_Add_Vector"] = Vector4.Zero
            },
            new Dictionary<string, HeadPreviewTexture>
            {
                [diffuse.ParameterName] = diffuse,
                [mask.ParameterName] = mask
            });
    }

    internal static HeadPreviewMaterial CreateLayeredSkinMaterial(
        string key,
        byte[] diffuseRgba,
        byte[] maskRgba,
        byte[]? addition,
        Vector4? addnColour = null,
        Vector4? blonde = null,
        byte[]? freckle = null,
        Vector4? freckleBlue = null,
        float freckleBlueScalar = 0,
        float transmissionScalar = 1,
        Vector4? scatterColour = null,
        Vector4? transmissionColour = null,
        bool isLe3 = false)
    {
        var textures = new Dictionary<string, HeadPreviewTexture>();
        var diffuse = CreateTexture("HED_Diff", diffuseRgba, TextureRole.Diffuse);
        var mask = CreateTexture("HED_Mask", maskRgba, TextureRole.Mask, true);
        textures[diffuse.ParameterName] = diffuse;
        textures[mask.ParameterName] = mask;
        if (addition is not null)
        {
            var addn = CreateTexture("HED_Addn", addition, TextureRole.Detail, true);
            textures[addn.ParameterName] = addn;
        }
        if (freckle is not null)
        {
            var frek = CreateTexture("HED_Frek", freckle, TextureRole.Detail);
            textures[frek.ParameterName] = frek;
        }

        return new HeadPreviewMaterial(
            key, key, HeadMaterialFamily.Skin, HeadMaterialBlendMode.Opaque, false,
            new Dictionary<string, float>
            {
                ["HED_TMis_Scalar"] = transmissionScalar,
                ["HED_Mask_Scalar"] = 1,
                ["HED_Addn_Blowout_Scalar"] = 1,
                ["HED_Addn_Colour_02_Scalar"] = 1,
                ["HED_Frek_BlueChannel_Scalar"] = freckleBlueScalar,
                ["HED_SPwr_Scalar"] = 8
            },
            new Dictionary<string, Vector4>
            {
                ["SkinTone"] = new Vector4(0.5f, 0.5f, 0.5f, 1),
                ["HED_Mask_Vector"] = Vector4.Zero,
                ["HED_Addn_Colour_Vector"] = addnColour ?? Vector4.Zero,
                ["blonde"] = blonde ?? Vector4.Zero,
                ["HED_Frek_BlueChannel_Vector"] = freckleBlue ?? Vector4.Zero,
                ["HED_Spec_Add_Vector"] = Vector4.Zero,
                ["SkinLightScattering"] = scatterColour ?? Vector4.Zero,
                ["HED_TClr_Vector"] = transmissionColour ?? Vector4.Zero
            },
            textures)
        {
            IsLe3 = isLe3
        };
    }

    internal static HeadPreviewMaterial CreateFemaleSkinMaterial(
        string key,
        float browStrength,
        float additionPower = 0,
        Vector4? specularColour = null,
        float blushStrength = 0,
        Vector4? blushColour = null)
    {
        var diffuse = CreateTexture("HED_Diff", [96, 72, 56, 255], TextureRole.Diffuse);
        var makeup = CreateTexture("HED_Makeup_Mask", [255, 0, 255, 255], TextureRole.Other);
        return new HeadPreviewMaterial(
            key,
            key,
            HeadMaterialFamily.Skin,
            HeadMaterialBlendMode.Opaque,
            false,
            new Dictionary<string, float>
            {
                ["HED_Brow_Tint_Scalar"] = browStrength,
                ["HED_EyeShadow_Tint_Scalar"] = 0,
                ["HED_Lips_Tint_Scalar"] = 0,
                ["HED_Addn_SPwr_Add_Scalar"] = additionPower,
                ["HED_Blush_Scalar"] = blushStrength,
                ["HED_SPwr_Scalar"] = 5
            },
            new Dictionary<string, Vector4>
            {
                ["SkinTone"] = new Vector4(0.5f, 0.5f, 0.5f, 1),
                ["HED_Brow_Tint_Vector"] = new Vector4(1, 0, 0, 1),
                ["HED_EyeShadow_Tint_Vector"] = Vector4.Zero,
                ["HED_Lips_Tint_Vector"] = Vector4.Zero,
                ["HED_Blush_Vector"] = blushColour ?? Vector4.One,
                ["HED_Spec_Add_Vector"] = specularColour ?? Vector4.Zero
            },
            new Dictionary<string, HeadPreviewTexture>
            {
                [diffuse.ParameterName] = diffuse,
                [makeup.ParameterName] = makeup
            });
    }

    internal static HeadPreviewMaterial CreateEyeMaterial(
        string key,
        byte[] diffuseRgba,
        byte[] maskRgba,
        Vector4 fxColour,
        float fxScalar,
        byte? primaryCubeValue = null,
        byte? secondaryCubeValue = null)
    {
        var diffuse = CreateTexture("EYE_Diff", diffuseRgba, TextureRole.Diffuse);
        var mask = CreateTexture("EYE_Mask", maskRgba, TextureRole.Mask);
        return new HeadPreviewMaterial(
            key, key, HeadMaterialFamily.Eyes, HeadMaterialBlendMode.Opaque, false,
            new Dictionary<string, float>
            {
                ["U_Offset"] = 0.0642f,
                ["V_Offset"] = -0.125f,
                ["HED_EYE_FX_Scalar"] = fxScalar
            },
            new Dictionary<string, Vector4>
            {
                ["EYE_Iris_Colour_Vector"] = new Vector4(0.1f, 0.2f, 0.3f, 1),
                ["EYE_White_Colour_Vector"] = Vector4.One,
                ["HED_EYE_FX_Vector"] = fxColour,
                ["EyeLightScattering"] = Vector4.Zero
            },
            new Dictionary<string, HeadPreviewTexture>
            {
                [diffuse.ParameterName] = diffuse,
                [mask.ParameterName] = mask
            })
        {
            FixedCubeTexture = CreateCube("primary", primaryCubeValue),
            SecondaryFixedCubeTexture = CreateCube("secondary", secondaryCubeValue)
        };

        static HeadPreviewCubeTexture? CreateCube(string name, byte? value)
        {
            if (value is null)
            {
                return null;
            }
            var face = new byte[] { value.Value, value.Value, value.Value, 255 };
            return new HeadPreviewCubeTexture(
                $"{name}:{value.Value}", 1,
                Enumerable.Range(0, 6).Select(_ => face.ToArray()).ToArray(),
                TextureColorSpace.Srgb);
        }
    }

    internal static HeadPreviewMaterial CreateLe2EyeMaterial(
        string key,
        byte[] maskRgba,
        Vector4 irisColour,
        Vector4 scleraColour)
    {
        var material = CreateEyeMaterial(
            key, [128, 128, 128, 255], maskRgba, Vector4.Zero, 0);
        var scalars = material.Scalars.ToDictionary(value => value.Key, value => value.Value);
        scalars["X_Tile"] = 0.9f;
        scalars["Y_Tile"] = 1.2f;
        scalars["Iris_Colour_Multiplier"] = 1;
        scalars["Primary_Reflection_Multiplier"] = 0;
        scalars["Secondary_Reflection_Multiplier"] = 0;
        scalars["Sclera_Darken"] = 1;
        var vectors = material.Vectors.ToDictionary(value => value.Key, value => value.Value);
        vectors["EYE_Iris_Colour_Vector"] = irisColour;
        vectors["EYE_White_Colour_Vector"] = scleraColour;
        return material with
        {
            Scalars = scalars,
            Vectors = vectors,
            SupportedScalars = scalars.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase),
            SupportedVectors = vectors.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase),
            SupportedTextures = new HashSet<string>(
                ["EYE_Diff", "EYE_Iris_Norm", "EYE_Lens_Norm", "EYE_Mask"],
                StringComparer.OrdinalIgnoreCase)
        };
    }

    internal static HeadPreviewMaterial CreateLashMaterial(
        string key,
        byte[] diffuseRgba,
        Vector4 colour,
        float specular)
    {
        var diffuse = CreateTexture("HED_Lash_Diff", diffuseRgba, TextureRole.Diffuse);
        return new HeadPreviewMaterial(
            key, key, HeadMaterialFamily.Lashes, HeadMaterialBlendMode.Translucent, true,
            new Dictionary<string, float>
            {
                ["HED_Lash_Opac_Scalar"] = 1,
                ["HED_Lash_Spec_Scalar"] = specular
            },
            new Dictionary<string, Vector4> { ["HED_Lash_Diff_Vector"] = colour },
            new Dictionary<string, HeadPreviewTexture> { [diffuse.ParameterName] = diffuse });
    }

    internal static HeadPreviewMaterial CreateHairMaterial(
        string key,
        byte[] diffuseRgba,
        bool meaningfulAlpha = true,
        float highlight1 = 400,
        float highlight2 = 25,
        Vector4? hairColour = null,
        string textureName = "HAIR_Diff",
        bool isLe3 = false)
    {
        var diffuse = CreateTexture(textureName, diffuseRgba, TextureRole.Diffuse, meaningfulAlpha);
        return new HeadPreviewMaterial(
            key,
            key,
            HeadMaterialFamily.Hair,
            HeadMaterialBlendMode.Translucent,
            true,
            new Dictionary<string, float>
            {
                ["Highlight1SpecExp_Scalar"] = highlight1,
                ["Highlight2SpecExp_Scalar"] = highlight2
            },
            new Dictionary<string, Vector4>
            {
                ["HED_Hair_Colour_Vector"] = hairColour ?? new Vector4(0.74f, 0.48f, 0.23f, 1)
            },
            new Dictionary<string, HeadPreviewTexture> { [diffuse.ParameterName] = diffuse })
        {
            IsLe3 = isLe3
        };
    }

    internal static HeadPreviewMaterial CreateMaskedHairMaterial(
        string key,
        byte[] opacityRgba,
        byte[] diffuseRgba,
        byte[] tangentRgba,
        byte[] specularRgba,
        bool isLe2 = false,
        bool isLe3 = false)
    {
        var textures = new Dictionary<string, HeadPreviewTexture>(StringComparer.OrdinalIgnoreCase)
        {
            ["__PROShort01_Opacity"] = CreateTexture(
                "__PROShort01_Opacity", opacityRgba, TextureRole.Mask),
            ["__PROShort01_Diffuse"] = CreateTexture(
                "__PROShort01_Diffuse", diffuseRgba, TextureRole.Diffuse),
            ["__PROShort01_Tangent"] = CreateTexture(
                "__PROShort01_Tangent", tangentRgba, TextureRole.Tangent),
            ["__PROShort01_Specular"] = CreateTexture(
                "__PROShort01_Specular", specularRgba, TextureRole.Specular)
        };
        return new HeadPreviewMaterial(
            key, key, HeadMaterialFamily.MaskedHair, HeadMaterialBlendMode.Masked, false,
            new Dictionary<string, float>(),
            new Dictionary<string, Vector4>(),
            textures)
        {
            IsLe2 = isLe2,
            IsLe3 = isLe3
        };
    }

    internal static HeadPreviewMaterial AddHairAuxiliaryMaps(
        HeadPreviewMaterial material,
        byte[] rgba)
    {
        var textures = new Dictionary<string, HeadPreviewTexture>(material.Textures, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, role) in new[]
                 {
                     ("HAIR_Norm", TextureRole.Normal),
                     ("HAIR_Mask", TextureRole.Mask),
                     ("HAIR_Tang", TextureRole.Tangent),
                     ("HAIR_SpecShift", TextureRole.Specular),
                     ("HAIR_SpecShift2", TextureRole.Specular)
                 })
        {
            textures[name] = CreateTexture(name, rgba, role);
        }
        return material with { Textures = textures };
    }
}
