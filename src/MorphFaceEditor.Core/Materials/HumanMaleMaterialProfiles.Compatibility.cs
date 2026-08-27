namespace MorphFaceEditor.Core.Materials;

/// <summary>
/// Compatibility facade retained for callers compiled against the Phase 4 API.
/// New code should use <see cref="HumanMaterialProfiles"/>, which covers both
/// supported LE1 human profiles.
/// </summary>
[Obsolete("Use HumanMaterialProfiles for LE1 Human Male and Human Female materials.")]
public static class HumanMaleMaterialProfiles
{
    public static IReadOnlyList<MaterialParameterDefinition> Definitions => HumanMaterialProfiles.Definitions;
    public static HeadMaterialFamily ClassifyMaster(string? name) => HumanMaterialProfiles.ClassifyMaster(name);
    public static HeadMaterialBlendMode BlendMode(HeadMaterialFamily family) => HumanMaterialProfiles.BlendMode(family);
    public static bool IsTwoSided(HeadMaterialFamily family) => HumanMaterialProfiles.IsTwoSided(family);
    public static MaterialParameterDefinition Describe(
        string name,
        MaterialParameterKind kind,
        HeadMaterialFamily fallbackFamily = HeadMaterialFamily.Unknown) =>
        HumanMaterialProfiles.Describe(name, kind, fallbackFamily);
    public static TextureRole InferTextureRole(string parameterName) => HumanMaterialProfiles.InferTextureRole(parameterName);
}
