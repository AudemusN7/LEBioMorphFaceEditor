namespace MorphFaceEditor.Core.Randomisation;

/// <summary>Hair geometry that can accompany each HMM scalp diffuse texture.</summary>
public static class HumanMaleHairScalpPolicy
{
    private static readonly IReadOnlySet<string> HairMorphNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Afro", "BuzzCut", "deiter", "flatTop", "Formal", "Geezer", "rollins", "Sarge",
        "Slick", "straightHairline", "widowsPeak", "Willis", "DEBUG_CapCorrector"
    };

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> CompatibleMorphs =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Afr"] = ["Afro", "deiter", "widowsPeak"],
            ["Gez"] = ["Geezer"],
            ["Rol"] = ["rollins", "flatTop"],
            ["Wil"] = ["Willis"],
            ["Spa"] = ["widowsPeak"]
        };

    /// <returns>
    /// Compatible morph names, an empty list for other HMM_HIR or bald/base HMM_HED scalp
    /// textures, or null when the selected texture is not a recognised HMM scalp diffuse.
    /// </returns>
    public static IReadOnlyList<string>? CompatibleMorphsForDiffuse(string? instancedPath)
    {
        if (string.IsNullOrWhiteSpace(instancedPath)) return null;
        var objectName = instancedPath[(instancedPath.LastIndexOf('.') + 1)..];
        const string prefix = "HMM_HIR_";
        const string suffix = "_Diff";
        if ((objectName.StartsWith("HMM_HED_PROBald_Scalp", StringComparison.OrdinalIgnoreCase) ||
             objectName.StartsWith("HMM_HED_PROBase_Scalp", StringComparison.OrdinalIgnoreCase)) &&
            objectName.Contains("Scalp", StringComparison.OrdinalIgnoreCase) &&
            objectName.Contains("Diff", StringComparison.OrdinalIgnoreCase)) return [];
        if (!objectName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !objectName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return null;

        var code = objectName[prefix.Length..^suffix.Length];
        return CompatibleMorphs.GetValueOrDefault(code) ?? [];
    }

    public static bool IsHairMorph(string name) => HairMorphNames.Contains(name);

    public static IReadOnlyDictionary<string, float>? CreateMorphOverrides(
        string? scalpDiffusePath,
        IReadOnlyCollection<string> editableHairMorphs,
        int seed)
    {
        var compatible = CompatibleMorphsForDiffuse(scalpDiffusePath);
        if (compatible is null) return null;
        var available = compatible.Where(name => editableHairMorphs.Contains(name, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var selected = available.Length == 0
            ? null
            : available[new MorphRandomiser.StableRandom(unchecked((ulong)(uint)seed ^ 0x484D4D48414952UL))
                .NextIndex(available.Length)];
        return editableHairMorphs.ToDictionary(
            name => name,
            name => name.Equals(selected, StringComparison.OrdinalIgnoreCase) ? 1f : 0f,
            StringComparer.OrdinalIgnoreCase);
    }
}
