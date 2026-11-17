namespace MorphFaceEditor.Core.Materials;

/// <summary>Parameters belonging to attachments rather than the head morph.</summary>
public static class HeadMorphMaterialParameterPolicy
{
    public const string AnonymousHairDiffuseParameter = "MaterialExpressionTextureSampleParameter2D_62";

    public static bool IsEditableHairDiffuseTexture(string controlName)
    {
        var name = MaterialParameterControlKey.ParameterName(controlName);
        return name.Equals("HAIR_Diff", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("HAIR_ADDN_Diff", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Hair_Stack", StringComparison.OrdinalIgnoreCase) ||
               name.Equals(AnonymousHairDiffuseParameter, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAttachmentOnlyTexture(string controlName)
    {
        var name = MaterialParameterControlKey.ParameterName(controlName);
        // This classification also clears the outgoing hairstyle's maps when
        // changing meshes. Editable diffuse overrides otherwise survive saves.
        return name.Equals("Diffuseuse", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("HAIR_", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("__PROShort01_", StringComparison.OrdinalIgnoreCase) ||
               IsEditableHairDiffuseTexture(name);
    }

    public static bool IsExcludedHeadTexture(string controlName) =>
        IsAttachmentOnlyTexture(controlName) && !IsEditableHairDiffuseTexture(controlName);

    public static MorphFaceMaterialOverrides ForHeadMorph(MorphFaceMaterialOverrides overrides) => overrides with
        {
            Textures = overrides.Textures.Where(value => !IsExcludedHeadTexture(value.Name)).ToArray()
        };
}
