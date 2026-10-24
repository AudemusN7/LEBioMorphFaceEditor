using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.Core.Services;

public sealed class MorphTargetCatalog
{
    private readonly object _sync = new();
    private readonly Dictionary<string, IReadOnlyList<MorphTargetAsset>> _targets =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<MorphTargetAsset> Load(MorphFaceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        // Static profiles have no authoring targets; their native geometry is retained.
        if (profile.TargetSetName is null) return [];
        lock (_sync)
        {
            var bundleKey = $"{profile.Game}.{profile.TargetSetName}";
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
        var resourceName = $"MorphFaceEditor.Core.Assets.MorphTargets.{bundleKey}.mft.br";
        using var stream = typeof(MorphTargetCatalog).Assembly.GetManifestResourceStream(resourceName)!;
        return MorphTargetBundleSerializer.Read(stream, bundleKey);
    }
}
