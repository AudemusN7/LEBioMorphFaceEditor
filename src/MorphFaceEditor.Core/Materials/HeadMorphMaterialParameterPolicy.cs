namespace MorphFaceEditor.Core.Materials;

/// <summary>Parameters belonging to attachments rather than the head morph.</summary>
public static class HeadMorphMaterialParameterPolicy
{
    public static bool IsAttachmentOnlyTexture(string controlName)
    {
        var name = MaterialParameterControlKey.ParameterName(controlName);
        // Hair maps come from the mesh's own MIC, including custom maps.
        // A face-wide override can otherwise put one hairstyle's maps on another.
        return name.Equals("Diffuseuse", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("HAIR_", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("__PROShort01_", StringComparison.OrdinalIgnoreCase);
    }

    public static MorphFaceMaterialOverrides ForHeadMorph(MorphFaceMaterialOverrides overrides) => overrides with
        {
            Textures = overrides.Textures.Where(value => !IsAttachmentOnlyTexture(value.Name)).ToArray()
        };
}
