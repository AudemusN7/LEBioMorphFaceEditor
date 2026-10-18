using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer.TextureRegistry;

namespace MorphFaceEditor.LegendaryExplorer;

/// <summary>Headmorph paths must have a real dynamically loadable source, even when selected from a level copy.</summary>
internal sealed class PlayerRuntimeAssetValidator(Func<MorphFaceGame, IEnumerable<string>>? packagePaths = null)
{
    internal void Verify(MorphFaceGame game, HeadMorphPayload head, HeadMorphPayload? preservedHead = null)
    {
        var preserved = References(preservedHead).GroupBy(Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var requested = References(head).Where(reference =>
        {
            var key = Key(reference);
            if (!preserved.TryGetValue(key, out var count) || count == 0) return true;
            preserved[key] = count - 1;
            return false;
        }).GroupBy(reference => reference.Path.Split('.')[0], StringComparer.OrdinalIgnoreCase).ToArray();
        if (requested.Length == 0) return;

        var references = requested.SelectMany(group => group).ToArray();
        var available = FindAvailable(game, references.Select(reference =>
            new AssetIdentity(string.Empty, reference.Path, 0, reference.ClassName)));
        var missing = references.FirstOrDefault(reference =>
            !available.Allows(new AssetIdentity(string.Empty, reference.Path, 0, reference.ClassName)));
        if (missing == default) return;
        throw UserFacingMessage.CreateError("loc_PlayerAsset_SeekFreeSourceRequired",
            $"No BIOG seekfree or guaranteed Player runtime source exists for {missing.ClassName} '{missing.Path}'.",
            missing.ClassName, missing.Path);
    }

    internal PlayerRuntimeAssetAvailability FindAvailable(MorphFaceGame game,
        IEnumerable<AssetIdentity> references, CancellationToken cancellationToken = default)
    {
        var requested = references.Select(PlayerRuntimeAssetAvailability.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (requested.Count == 0) return new(game, available);
        LegendaryExplorerCoreRuntime.Initialize();
        var meGame = LecTextureRegistryPackageScanner.ToMeGame(game);
        var globals = EntryImporter.FilesSafeToImportFrom(meGame).Concat(EntryImporter.FilesSafeToImportFromPostLoad(meGame))
            .Append("BIOG_MORPH_FACE.pcc").Select(Path.GetFileNameWithoutExtension)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = requested.Select(key => key.Split('|')[1].Split('.')[0]).Concat(globals)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in (packagePaths ?? KnownPackages)(game)
                     .Where(path => names.Contains(Path.GetFileNameWithoutExtension(path)) &&
                         Path.GetExtension(path).Equals(".pcc", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Read each relevant package's tables once, without mesh/texture bodies.
            using var package = MEPackageHandler.UnsafePartialLoad(path, _ => false);
            if (package.Game != meGame) continue;
            var global = globals.Contains(Path.GetFileNameWithoutExtension(path));
            var biog = Path.GetFileNameWithoutExtension(path).StartsWith("BIOG_", StringComparison.OrdinalIgnoreCase);
            if (!global && !biog) continue;
            foreach (var matches in package.Exports.Where(export => export.ClassName is "Texture2D" or "SkeletalMesh")
                         .GroupBy(export => $"{export.ClassName}|{export.MemoryFullPath}", StringComparer.OrdinalIgnoreCase))
            {
                if (!requested.Contains(matches.Key) || matches.Count() != 1) continue;
                if (global || HasSeekFreeAncestry(matches.First())) available.Add(matches.Key);
            }
        }
        return new(game, available);
    }

    private static bool HasSeekFreeAncestry(ExportEntry export)
    {
        var visited = new HashSet<int>();
        for (IEntry? entry = export; entry is not null; entry = entry.Parent)
        {
            if (!visited.Add(entry.UIndex) || entry is ExportEntry { IsForcedExport: true }) return false;
            if (entry.idxLink != 0 && entry.Parent is null) return false;
        }
        return true;
    }

    internal static IEnumerable<string> KnownPackages(MorphFaceGame game)
    {
        // Include mounted mod BIOGs and deliberately registered external packages.
        // Registry membership supplies a location, never proof of seekfree storage.
        var loaded = MELoadedFiles.GetFilesLoadedInGame(LecTextureRegistryPackageScanner.ToMeGame(game), forceReload: true);
        var store = new TextureRegistryStore(TextureRegistryPaths.CreateDefault());
        return loaded.Values.Concat(store.ReadManualWithStatus(game).Snapshot?.ManualAssets
            .Where(asset => !asset.IsMissing && Path.GetFileNameWithoutExtension(asset.PackagePath)
                .StartsWith("BIOG_", StringComparison.OrdinalIgnoreCase) &&
                !Path.GetFileName(asset.PackagePath).Equals("BIOG_MORPH_FACE.pcc", StringComparison.OrdinalIgnoreCase))
            .Select(asset => asset.PackagePath) ?? []);
    }

    private static string Key((string Slot, string Path, string ClassName) reference) =>
        $"{reference.Slot}|{reference.ClassName}|{reference.Path}";

    private static IEnumerable<(string Slot, string Path, string ClassName)> References(HeadMorphPayload? head)
    {
        if (head is null) yield break;
        if (!string.IsNullOrWhiteSpace(head.HairMesh) && !head.HairMesh.Equals("None", StringComparison.OrdinalIgnoreCase))
            yield return ("Hair", head.HairMesh, "SkeletalMesh");
        for (var i = 0; i < head.AccessoryMeshes.Count; i++)
            if (!head.AccessoryMeshes[i].Equals("None", StringComparison.OrdinalIgnoreCase))
                yield return ($"Accessory:{i}", head.AccessoryMeshes[i], "SkeletalMesh");
        foreach (var value in head.MaterialData.Textures)
            if (value.TextureReference is { } texture && !texture.InstancedPath.Equals("None", StringComparison.OrdinalIgnoreCase))
                yield return ($"Texture:{value.Name}", texture.InstancedPath, "Texture2D");
    }
}

/// <summary>Exact runtime identities proven available for one Player catalogue refresh.</summary>
public sealed class PlayerRuntimeAssetAvailability
{
    private readonly IReadOnlySet<string> _keys;
    internal PlayerRuntimeAssetAvailability(MorphFaceGame game, IReadOnlySet<string> keys) { Game = game; _keys = keys; }
    public MorphFaceGame Game { get; }
    public bool Allows(AssetIdentity identity) => _keys.Contains(Key(identity));
    internal static string Key(AssetIdentity identity) =>
        $"{identity.ClassName}|{TextureCatalogPicker.CanonicalPath(identity.InstancedPath, identity.PackagePath)}";
}
