namespace MorphFaceEditor.Core.Domain;

/// <summary>Names eligible for installed head-attachment discovery and picker choices.</summary>
public static class AttachmentMeshNamePolicy
{
    private static readonly string[] ExcludedSuffixes =
        ["_CC", "_Copy", "_old", "_Remaster", "_Review", "_Test"];

    public static bool HasExcludedSuffix(string instancedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instancedPath);
        var objectName = instancedPath.Split('.').Last();
        return ExcludedSuffixes.Any(suffix =>
            objectName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsInstalledHeadAttachment(string instancedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instancedPath);
        return instancedPath.Split('.').Last().Contains("HIR", StringComparison.OrdinalIgnoreCase) &&
               !HasExcludedSuffix(instancedPath);
    }
}
