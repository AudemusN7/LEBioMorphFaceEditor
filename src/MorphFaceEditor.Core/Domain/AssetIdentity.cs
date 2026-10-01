namespace MorphFaceEditor.Core.Domain;

/// <summary>
/// Stable-enough provenance for a package entry. UIndex is retained only as a lookup hint.
/// </summary>
public sealed record AssetIdentity(
    string PackagePath,
    string InstancedPath,
    int UIndex,
    string ClassName,
    bool IsImport = false)
{
    // Records a texture picker action, including a donor in the opened PCC.
    // Ordinary decoded/authored references retain the canonical donor policy.
    public bool IsExplicitTextureDonor { get; init; }
}
