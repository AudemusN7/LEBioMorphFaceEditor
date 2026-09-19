namespace MorphFaceEditor.LegendaryExplorer;

/// <summary>
/// Converts an exact donor occurrence into the identity that a normal PCC must
/// materialise. Seek-free packages omit their own linker name from export
/// paths; packaged files must restore that package root. Player/TSE RON paths
/// deliberately do not use this policy.
/// </summary>
internal static class PccAssetPathPolicy
{
    internal static string FromDonorOccurrence(string instancedPath, string donorPackagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instancedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(donorPackagePath);

        var packageName = Path.GetFileNameWithoutExtension(donorPackagePath);
        if (string.IsNullOrWhiteSpace(packageName) ||
            instancedPath.Equals(packageName, StringComparison.OrdinalIgnoreCase) ||
            instancedPath.StartsWith($"{packageName}.", StringComparison.OrdinalIgnoreCase) ||
            HasNativeGlobalRoot(instancedPath))
        {
            return instancedPath;
        }

        return $"{packageName}.{instancedPath}";
    }

    private static bool HasNativeGlobalRoot(string instancedPath) =>
        instancedPath.StartsWith("BIO", StringComparison.OrdinalIgnoreCase) ||
        instancedPath.StartsWith("Core.", StringComparison.OrdinalIgnoreCase) ||
        instancedPath.StartsWith("Engine.", StringComparison.OrdinalIgnoreCase) ||
        instancedPath.StartsWith("SFXGame.", StringComparison.OrdinalIgnoreCase) ||
        instancedPath.StartsWith("EffectsMaterials.", StringComparison.OrdinalIgnoreCase) ||
        instancedPath.StartsWith("EngineMaterials.", StringComparison.OrdinalIgnoreCase);
}
