namespace MorphFaceEditor.Core.Domain;

/// <summary>
/// Stable-enough provenance for a package entry. UIndex is retained only as a lookup hint.
/// </summary>
public sealed record AssetIdentity(
    string PackagePath,
    string InstancedPath,
    int UIndex,
    string ClassName,
    bool IsImport = false);
