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
        if (IsCcHairMesh(instancedPath)) return false;
        return ExcludedSuffixes.Any(suffix =>
            objectName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsCcHairMesh(string instancedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instancedPath);
        var objectName = instancedPath.Split('.').Last();
        return objectName.EndsWith("_CC", StringComparison.OrdinalIgnoreCase) &&
               objectName.Split('_').Any(token => token.Equals("HIR", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsInstalledHeadAttachment(string instancedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instancedPath);
        var objectName = instancedPath.Split('.').Last();
        return objectName.Split('_').Any(token =>
                   token.Equals("HIR", StringComparison.OrdinalIgnoreCase) ||
                   token.Equals("HAT", StringComparison.OrdinalIgnoreCase)) &&
               !HasExcludedSuffix(instancedPath) && !IsExcludedNativeAttachment(instancedPath);
    }

    /// <summary>Reviewed malformed, duplicate or unsupported native attachment identities.</summary>
    public static bool IsExcludedNativeAttachment(string instancedPath) =>
        instancedPath.Equals("BIOG_HMF_HIR_PRO.Mira.HMF_HIR_MIR_LOD0", StringComparison.OrdinalIgnoreCase) ||
        instancedPath.Equals("BIOG_HMF_HIR_PRO.PROCustomBraids01.HMM_HIR_PROCustomBraids01_MDL_LOD0", StringComparison.OrdinalIgnoreCase) ||
        instancedPath.Equals("BIOG_HMF_HIR_PRO.Hair_PROCustomBraids01.HMM_HIR_PROCustomBraids01_MDL_LOD0", StringComparison.OrdinalIgnoreCase) ||
        instancedPath.Equals("BIOG_HMF_HIR_PRO.Hair_VI.HMF_HIR_VI_MDL", StringComparison.OrdinalIgnoreCase);

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
