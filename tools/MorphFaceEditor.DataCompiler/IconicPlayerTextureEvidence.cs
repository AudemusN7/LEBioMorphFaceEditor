using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.DataCompiler;

// Evidence comes from the installed official iconic mesh and its used materials,
// not from a player's RON or an independently selected replacement texture.
internal static class IconicPlayerTextureEvidence
{
    internal sealed record Decision(string SourceGame, string TargetGame, string SourcePath, string TargetPath, int EvidenceCount);
    private sealed record Head(Dictionary<string, string> Textures, Dictionary<string, string[]> Bindings);

    internal static IEnumerable<Decision> Build((string Source, string Target)[] pairs)
    {
        var heads = new Dictionary<(string Game, string Sex), Head>();
        foreach (var game in new[] { "LE1", "LE2", "LE3" })
        foreach (var sex in new[] { "HMF", "HMM" })
            heads[(game, sex)] = ReadHead(game, sex);

        foreach (var (sourceGame, targetGame) in pairs)
        foreach (var sex in new[] { "HMF", "HMM" })
        {
            var source = heads[(sourceGame, sex)];
            var target = heads[(targetGame, sex)];
            var matches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // Same relative object identity is stronger evidence than a MIC's
            // changed default, especially for the iconic stack normal.
            foreach (var (relative, path) in source.Textures)
                if (target.Textures.TryGetValue(relative, out var counterpart)) matches[path] = counterpart;

            var evidence = source.Bindings.Where(pair => pair.Value.Length == 1 &&
                    target.Bindings.TryGetValue(pair.Key, out var values) && values.Length == 1)
                .Select(pair => (Source: pair.Value[0], Target: target.Bindings[pair.Key][0]))
                .GroupBy(pair => pair.Source, StringComparer.OrdinalIgnoreCase);
            foreach (var group in evidence)
            {
                var targets = group.Select(pair => pair.Target).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (!matches.ContainsKey(group.Key) && targets.Length == 1) matches[group.Key] = targets[0];
            }
            foreach (var (sourcePath, targetPath) in matches)
                if (!sourcePath.Equals(targetPath, StringComparison.OrdinalIgnoreCase))
                    yield return new Decision(sourceGame, targetGame, sourcePath, targetPath, 1);
        }
    }

    private static Head ReadHead(string game, string sex)
    {
        var packageName = sex == "HMF" ? "BIOG_HMF_HED_PROMorph_R" : "BIOG_HMM_HED_PROMorph";
        var meshPath = sex == "HMM" ? $"{packageName}.Sheppard.HMM_HED_PROSheppard_MDL" :
            game == "LE3" ? $"{packageName}.PROShepard.HMF_HED_PROShepard_MDL" :
            $"{packageName}.PROSheppard.HMF_HED_PROSheppard_MDL";
        var gameId = Enum.Parse<MorphFaceGame>(game);
        var packagePath = InstalledGameAssetResolver.ResolvePackage(gameId, packageName + ".pcc");
        using var package = MEPackageHandler.OpenMEPackage(packagePath, forceLoadFromDisk: true);
        var ownerPath = meshPath[..(meshPath.LastIndexOf('.') + 1)];
        string Canonical(ExportEntry entry) => PccAssetPathPolicy.FromExport(entry);
        var textures = package.Exports.Where(entry => entry.ClassName == "Texture2D" &&
                Canonical(entry).StartsWith(ownerPath, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(entry => Canonical(entry)[ownerPath.Length..], Canonical, StringComparer.OrdinalIgnoreCase);
        var texturePaths = textures.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (textures.Count == 0) throw new InvalidDataException($"No iconic textures in {game} {ownerPath}");
        var meshEntry = package.Exports.Single(entry => entry.ClassName == "SkeletalMesh" &&
            Canonical(entry).Equals(meshPath, StringComparison.OrdinalIgnoreCase));
        var mesh = meshEntry.GetBinaryData<SkeletalMesh>();
        if (mesh.LODModels.Length == 0) throw new InvalidDataException($"No LOD0 for {meshPath}");
        var indices = MorphFacePackageReader.ResolveUsedMaterialSections(meshEntry, mesh, mesh.Materials.Length)
            .Where(pair => pair.Value.Any(section => section.LodIndex == 0)).Select(pair => pair.Key);
        var bindings = new List<(string Name, string Path)>();
        foreach (var index in indices)
        {
            if (index < 0 || index >= mesh.Materials.Length || package.GetEntry(mesh.Materials[index]) is not ExportEntry material)
                throw new InvalidDataException($"Missing local LOD0 material {index} for {meshPath}");
            var parameters = material.GetProperty<ArrayProperty<StructProperty>>("TextureParameterValues");
            if (parameters is null) continue;
            foreach (var value in parameters)
            {
                var name = value.GetProp<NameProperty>("ParameterName")?.Value.Instanced;
                var texture = value.GetProp<ObjectProperty>("ParameterValue")?.ResolveToEntry(package) as ExportEntry;
                if (name is not null && texture is not null && texture.ClassName == "Texture2D" && texturePaths.Contains(Canonical(texture)))
                    bindings.Add((name, Canonical(texture)));
            }
        }
        if (bindings.Count == 0) throw new InvalidDataException($"No iconic material texture bindings for {meshPath}");
        return new Head(textures, bindings.GroupBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(value => value.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase));
    }
}
