using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.Services;

public sealed class MorphTargetCatalog
{
    private readonly object _sync = new();
    private readonly Dictionary<string, IReadOnlyList<MorphTargetAsset>> _targets =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<MorphTargetAsset> Load(
        MorphFaceProfile profile,
        MorphFaceGame game)
    {
        ArgumentNullException.ThrowIfNull(profile);
        lock (_sync)
        {
            var bundleKey = $"{game}.{profile.TargetSetName}";
            if (_targets.TryGetValue(bundleKey, out var cached))
            {
                return cached;
            }

            var loaded = LoadBundle(bundleKey);
            _targets[bundleKey] = loaded;
            return loaded;
        }
    }

    private static IReadOnlyList<MorphTargetAsset> LoadBundle(string bundleKey)
    {
        var resourceName = $"MorphFaceEditor.Assets.MorphTargets.{bundleKey}.mft.br";
        using var stream = typeof(MorphTargetCatalog).Assembly.GetManifestResourceStream(resourceName)!;
        return MorphTargetBundleSerializer.Read(stream, bundleKey);
    }
}
