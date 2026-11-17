using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.LegendaryExplorer;

/// <summary>
/// Reads locally supplied reference heads from family/archetype/game.ron.
/// The family folder pairs independently authored game counterparts. The caller
/// still verifies the source bake against its candidate reference and Player rig.
/// </summary>
public sealed class PlayerMorphBaselineCatalog(string root)
{
    private readonly string _root = Path.GetFullPath(root);

    // User-authored files live outside the LocalAppData directories cleared by the editor.
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LE BioMorphFace Editor", "PlayerBaselines");

    public PlayerMorphBaselineCatalog() : this(DefaultRoot) { }

    public IReadOnlyList<PlayerMorphConversionBaseline> ForProfile(MorphFaceProfile profile) =>
        ReadFamilies(profile, null);

    public PlayerMorphConversionBaseline? FindCounterpart(string baselineId, MorphFaceProfile targetProfile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baselineId);
        return ReadFamilies(targetProfile, baselineId).SingleOrDefault();
    }

    private List<PlayerMorphConversionBaseline> ReadFamilies(MorphFaceProfile profile, string? familyId)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Archetype is not ("human-female" or "human-male") ||
            profile.Game is not (MorphFaceGame.LE1 or MorphFaceGame.LE2 or MorphFaceGame.LE3)) return [];

        DirectoryInfo[] families;
        try
        {
            var rootDirectory = new DirectoryInfo(_root);
            if (!IsLocalEntry(rootDirectory)) return [];
            families = rootDirectory.GetDirectories();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }

        List<PlayerMorphConversionBaseline> result = [];
        var names = StringComparer.OrdinalIgnoreCase;
        foreach (var group in families.GroupBy(dir => dir.Name, names).OrderBy(group => group.Key, names))
        {
            // Discover IDs from the root. Never interpret a supplied ID as a path.
            if ((familyId is not null && !names.Equals(familyId, group.Key)) || group.Count() != 1) continue;
            var family = group.First();
            var archetype = new DirectoryInfo(Path.Combine(family.FullName, profile.Archetype));
            var file = new FileInfo(Path.Combine(archetype.FullName, $"{profile.Game}.ron"));
            try
            {
                if (!IsLocalEntry(family) || !IsLocalEntry(archetype) || !IsLocalEntry(file)) continue;
                var reference = TseHeadMorphRon.ReadDocument(file.FullName);
                if (reference.Provenance is { } tag &&
                    (!tag.PlayerMorph || tag.Game != profile.Game || !names.Equals(tag.Archetype, profile.ExportTag.Trim('[', ']'))))
                    continue;
                var morphData = reference.Head.MorphData;
                if (morphData.BakedLods.Count > 0)
                    result.Add(new(family.Name, profile.Key, morphData));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // A missing, malformed or partially written optional head cannot identify a family.
            }
        }
        return result;
    }

    private static bool IsLocalEntry(FileSystemInfo entry) =>
        entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) == 0;
}
