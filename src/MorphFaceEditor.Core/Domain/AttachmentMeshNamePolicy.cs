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
        var objectName = instancedPath.Split('.').Last();
        return objectName.Split('_').Any(token =>
                   token.Equals("HIR", StringComparison.OrdinalIgnoreCase) ||
                   token.Equals("HAT", StringComparison.OrdinalIgnoreCase)) &&
               !HasExcludedSuffix(instancedPath);
    }

    public static bool IsBaseHeadMesh(string instancedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instancedPath);
        var objectName = instancedPath.Split('.').Last();
        return objectName.Contains("_HED_PROBase_MDL", StringComparison.OrdinalIgnoreCase) ||
               objectName.Contains("_HED_PROCustom_MDL", StringComparison.OrdinalIgnoreCase) ||
               objectName.Contains("_HED_PROAverage_MDL", StringComparison.OrdinalIgnoreCase) ||
               objectName.Contains("_HED_PROKaidan_MDL", StringComparison.OrdinalIgnoreCase) ||
               objectName.StartsWith("HMF_Blank_World", StringComparison.OrdinalIgnoreCase);
    }
}
