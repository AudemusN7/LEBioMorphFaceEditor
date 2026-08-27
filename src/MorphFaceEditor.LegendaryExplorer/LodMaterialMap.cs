namespace MorphFaceEditor.LegendaryExplorer;

/// <summary>Resolves a section-local material index through an Unreal skeletal mesh LOD material map.</summary>
internal static class LodMaterialMap
{
    public static int Resolve(
        int localMaterialIndex,
        IReadOnlyList<int> materialMap,
        int materialCount,
        string context)
    {
        if ((uint)localMaterialIndex >= (uint)materialMap.Count)
        {
            throw new InvalidDataException(
                $"{context} references local material {localMaterialIndex}, outside its {materialMap.Count}-entry LOD map.");
        }
        var resolved = materialMap[localMaterialIndex];
        return resolved >= 0 && resolved < materialCount
            ? resolved
            : throw new InvalidDataException(
                $"{context} maps local material {localMaterialIndex} to invalid global slot {resolved}.");
    }
}
