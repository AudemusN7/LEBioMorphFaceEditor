using MorphFaceEditor.Core.Materials;
using SharpDX;

namespace MorphFaceEditor.Rendering;

internal readonly partial record struct HeadPreviewMaterialBindings
{
    private static HeadPreviewMaterialBindings CreateAttachmentBindings(HeadPreviewMaterial material)
    {
        HeadPreviewTexture? Named(string name) => SelectByName(material, name);
        return material.PreviewModel switch
        {
            HumanMaterialPreviewModel.Hat => new(Named("HMN_HAT_Diff"), Named("HMN_HAT_Norm"), null, null, null, null, null, null, null, null),
            HumanMaterialPreviewModel.Visor => new(null, Named("HMM_VSR_Norm"), null, null, null, null, null, null, null, null),
            HumanMaterialPreviewModel.CapDecal => new(Named("Cap_Diff"), Named("Cap_Norm"), null, null, null, null, null, null, null, null),
            HumanMaterialPreviewModel.CapLe2 => new(Named("__CapCrewDiffuse"), Named("HMN_HAT_Norm"), Named("HMN_HAT_Diff"), null, null, null, null, null, null, null),
            HumanMaterialPreviewModel.AndersonHat or HumanMaterialPreviewModel.MotoHat =>
                new(Named("Diffuse_Map"), Named("Normal_Map"), Named("Specular_Map"), null, null, null, null, null, null, null),
            HumanMaterialPreviewModel.VisorLens => new(
                Named("__LensScanline"), Named("__LensEmission"), Named("__LensDistortion"), Named("__LensFractal"),
                null, null, null, null, null, null),
            _ => new(null, null, null, null, null, null, null, null, null, null)
        };
    }
}

internal partial struct HeadPreviewMaterialConstants
{
    private static HeadPreviewMaterialConstants CreateAttachmentConstants(
        HeadPreviewMaterial material, HeadPreviewMaterialBindings bindings) => new()
    {
        BaseColor = material.PreviewModel == HumanMaterialPreviewModel.VisorLens
            ? Vector4.Zero // Native SelectionColor is zero in the standalone preview.
            : GetVector(material, material.PreviewModel == HumanMaterialPreviewModel.Visor ? "VSR_Colour_01" : "HMN_HAT_Tint1_Vector",
                material.PreviewModel == HumanMaterialPreviewModel.Visor ? new Vector4(0, 0, 0, 1) : Vector4.One),
        SecondaryColor = material.PreviewModel == HumanMaterialPreviewModel.VisorLens
            ? Vector4.Zero // The renderer supplies the scanline phase each frame.
            : GetVector(material, material.PreviewModel == HumanMaterialPreviewModel.Visor ? "VSR_Colour_02" : "HMN_HAT_Tint2_Vector",
                material.PreviewModel == HumanMaterialPreviewModel.Visor ? new Vector4(0.5f, 0.5f, 0.5f, 1) : Vector4.One),
        TertiaryColor = material.PreviewModel == HumanMaterialPreviewModel.VisorLens
            ? Vector4.Zero // The renderer supplies the distortion phase each frame.
            : GetVector(material, "None", Vector4.One),
        QuaternaryColor = Vector4.Zero, // The renderer supplies the lens fractal phase each frame.
        SpecularColor = material.PreviewModel == HumanMaterialPreviewModel.Hat
            ? GetVector(material, "HMN_HAT_Spec_Colour_Vector", Vector4.One)
            : GetVector(material, "Phong_Spec_Color", Vector4.Zero),
        EyeEmissiveColor = GetVector(material, "Emissive_color", Vector4.Zero),
        ScatterColor = GetVector(material, "SkinLightScattering", Vector4.Zero),
        SurfaceParameters = new Vector4(0, 0, 0,
            material.BlendMode == HeadMaterialBlendMode.Masked ? 0.3333f : 0),
        TextureFlags0 = new Vector4(bindings.Diffuse is null ? 0 : 1,
            bindings.Normal is null ? 0 : 1, bindings.Mask is null ? 0 : 1, bindings.Detail is null ? 0 : 1),
        TextureFlags1 = new Vector4(bindings.Auxiliary1 is null ? 0 : 1,
            material.FixedCubeTexture is null ? 0 : 1,
            material.SecondaryFixedCubeTexture is null ? 0 : 1, 0),
        TextureFlags2 = new Vector4(material.TwoSided ? 1 : 0, 0, (float)material.PreviewModel, 0),
        GeneralParameters = material.PreviewModel == HumanMaterialPreviewModel.VisorLens
            ? new Vector4(1, 1, 0, 0) // Neutral dynamic opacity; the shader receives one.
            : new Vector4(
            GetScalar(material, material.PreviewModel switch
            {
                HumanMaterialPreviewModel.Hat => "CubeMap_Intensity_Scalar",
                HumanMaterialPreviewModel.Visor => "VSR_CubeMap_Intensity",
                _ => "CubeMap_Intensity"
            }, material.PreviewModel == HumanMaterialPreviewModel.Visor ? 10 : 0),
            GetScalar(material, material.PreviewModel == HumanMaterialPreviewModel.Visor ? "VSR_Opacity" : "Cap_Opac_Scalar",
                material.PreviewModel == HumanMaterialPreviewModel.Visor ? 0.8f : 1), GetScalar(material, "Phong_Spwr", 5),
            GetScalar(material, "Emissive_Blowout", 0)),
        EyeParameters2 = new Vector4(0, 0, material.IsLe2 ? 1 : 0, material.IsLe3 ? 1 : 0)
    };
}
