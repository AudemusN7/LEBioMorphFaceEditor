namespace MorphFaceEditor.Core.Materials;

/// <summary>Parameters belonging to attachments rather than the head morph.</summary>
public static class HeadMorphMaterialParameterPolicy
{
    public static bool IsAttachmentOnlyTexture(string controlName) =>
        MaterialParameterControlKey.ParameterName(controlName)
            .Equals("Diffuseuse", StringComparison.OrdinalIgnoreCase);
}
