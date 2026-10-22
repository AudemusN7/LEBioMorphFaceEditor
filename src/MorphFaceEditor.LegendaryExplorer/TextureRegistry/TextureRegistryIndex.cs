using System.Runtime.CompilerServices;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.LegendaryExplorer.TextureRegistry;

/// <summary>Shared policy views of one parsed registry; contains no open packages.</summary>
internal sealed class TextureRegistryIndex
{
    private static readonly ConditionalWeakTable<TextureRegistrySnapshot, TextureRegistryIndex> Indices = new();
    private readonly Lazy<Dictionary<string, InstalledAssetOccurrence[]>> _officialAssets;

    private TextureRegistryIndex(TextureRegistrySnapshot snapshot)
    {
        OfficialPackages = snapshot.OfficialPackagePaths.Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _officialAssets = new(() => snapshot.Assets.Where(asset =>
                (asset.Origin is TextureCatalogOrigin.BaseGame or TextureCatalogOrigin.OfficialDlc) &&
                OfficialPackages.Contains(Path.GetFullPath(asset.PackagePath)))
            .GroupBy(asset => Key(asset.ClassName, asset.InstancedPath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(asset => asset.MountPriority)
                .ThenBy(asset => asset.PackagePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(asset => asset.ExportUIndex).ToArray(), StringComparer.OrdinalIgnoreCase));
    }

    internal static TextureRegistryIndex For(TextureRegistrySnapshot snapshot) =>
        Indices.GetValue(snapshot, static value => new(value));

    internal IReadOnlySet<string> OfficialPackages { get; }
    internal IReadOnlyList<InstalledAssetOccurrence> OfficialOccurrences(string path, string className) =>
        _officialAssets.Value.GetValueOrDefault(Key(className, path), []);

    private static string Key(string className, string path) => $"{className}|{path}";
}
