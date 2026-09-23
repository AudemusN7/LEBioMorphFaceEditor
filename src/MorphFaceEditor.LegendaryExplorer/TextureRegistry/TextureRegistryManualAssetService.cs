using LegendaryExplorerCore.Packages;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.LegendaryExplorer.TextureRegistry;

/// <summary>Appends explicitly selected exports without applying automatic discovery filters.</summary>
public sealed class TextureRegistryManualAssetService(TextureRegistryStore store)
{
    public TextureRegistryStatus Append(
        MorphFaceGame game,
        IReadOnlyList<ManualRegistryAsset> selections,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selections);
        if (selections.Count == 0) throw new ArgumentException("Select at least one asset.", nameof(selections));
        if (store.GetStatus(game).State != TextureRegistryState.Ready)
            throw new InvalidDataException($"Build the {game} installed database before adding custom assets.");
        var stored = store.ReadManualWithStatus(game);
        if (stored.Status.State is not (TextureRegistryState.Ready or TextureRegistryState.Missing))
            throw new InvalidDataException($"The custom asset database is {stored.Status.State}: {stored.Status.ErrorMessage}");
        var snapshot = PruneRelinked(stored.Snapshot ?? Empty(game), selections);
        var additions = selections.Where(selection => !snapshot.ManualAssets.Any(existing =>
            Key(existing).Equals(Key(selection), StringComparison.OrdinalIgnoreCase))).ToArray();
        if (additions.Length == 0) return stored.Status;
        var merged = Apply(game, snapshot, additions, snapshot, cancellationToken, out var failures);
        if (failures.Count > 0)
            throw new InvalidDataException(string.Join(Environment.NewLine, failures));
        store.WriteManualAtomic(merged, cancellationToken);
        _ = Revalidate(game, cancellationToken);
        return store.ReadManualWithStatus(game).Status;
    }

    public IReadOnlyList<string> Revalidate(MorphFaceGame game, CancellationToken cancellationToken = default)
    {
        var stored = store.ReadManualWithStatus(game);
        if (stored.Status.State == TextureRegistryState.Missing) return [];
        if (stored.Status.State != TextureRegistryState.Ready || stored.Snapshot is null)
            throw new InvalidDataException($"The custom database is {stored.Status.State}: {stored.Status.ErrorMessage}");
        var previous = stored.Snapshot;
        if (previous.ManualAssets.Count == 0) return [];
        var rebuilt = Apply(game, Empty(game), previous.ManualAssets,
            previous, cancellationToken, out var failures);
        store.WriteManualAtomic(rebuilt, cancellationToken);
        var reportPath = store.GetManualReportPath(game);
        if (failures.Count > 0)
            File.WriteAllLines(reportPath,
                [$"{game} custom assets could not be relinked during rebuild.",
                 "Use Add Custom Asset to select the package again, or restore the original PCC path.",
                 "", .. failures]);
        else if (File.Exists(reportPath))
            File.Delete(reportPath);
        return failures;
    }

    internal static TextureRegistrySnapshot Combine(TextureRegistrySnapshot installed,
        TextureRegistrySnapshot manual)
    {
        var textures = installed.Candidates.ToDictionary(value => value.InstancedPath,
            StringComparer.OrdinalIgnoreCase);
        foreach (var custom in manual.Candidates)
        {
            var existing = textures.GetValueOrDefault(custom.InstancedPath);
            var occurrences = (existing?.Occurrences ?? []).Concat(custom.Occurrences)
                .DistinctBy(value => SourceKey(value.PackagePath, value.ExportUIndex),
                    StringComparer.OrdinalIgnoreCase).ToArray();
            textures[custom.InstancedPath] = custom with
            {
                Occurrences = occurrences,
                EffectiveOccurrence = ChooseTexture(occurrences)
            };
        }
        var meshes = installed.AttachmentMeshes.ToDictionary(value => value.CanonicalPath,
            StringComparer.OrdinalIgnoreCase);
        foreach (var custom in manual.AttachmentMeshes)
        {
            var existing = meshes.GetValueOrDefault(custom.CanonicalPath);
            var occurrences = (existing?.Occurrences ?? []).Concat(custom.Occurrences)
                .DistinctBy(value => SourceKey(value.PackagePath, value.ExportUIndex),
                    StringComparer.OrdinalIgnoreCase).ToArray();
            meshes[custom.CanonicalPath] = custom with
            {
                Occurrences = occurrences,
                EffectiveOccurrence = ChooseMesh(occurrences)
            };
        }
        return installed with
        {
            Candidates = textures.Values.OrderBy(value => value.InstancedPath,
                StringComparer.OrdinalIgnoreCase).ToArray(),
            AttachmentMeshes = meshes.Values.OrderBy(value => value.CanonicalPath,
                StringComparer.OrdinalIgnoreCase).ToArray(),
            ManualAssets = manual.ManualAssets
        };
    }

    private static TextureRegistrySnapshot Empty(MorphFaceGame game) => new(
        TextureRegistrySnapshot.CurrentSchemaVersion, ToCatalogGame(game), DateTimeOffset.UtcNow, 0, []);

    private static TextureRegistrySnapshot PruneRelinked(TextureRegistrySnapshot snapshot,
        IReadOnlyList<ManualRegistryAsset> selections)
    {
        var replaced = snapshot.ManualAssets.Where(old => old.IsMissing && selections.Any(newAsset =>
            old.ClassName.Equals(newAsset.ClassName, StringComparison.OrdinalIgnoreCase) &&
            old.InstancedPath.Equals(newAsset.InstancedPath, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (replaced.Length == 0) return snapshot;
        bool Keep(string path, int index) => !replaced.Any(old =>
            SameSource(old.PackagePath, old.ExportUIndex, path, index));
        var textures = snapshot.Candidates.Select(candidate =>
            candidate with
            {
                Occurrences = candidate.Occurrences.Where(value => Keep(value.PackagePath, value.ExportUIndex)).ToArray()
            }).Where(candidate => candidate.Occurrences.Count > 0)
            .Select(candidate => candidate with
            {
                EffectiveOccurrence = ChooseTexture(candidate.Occurrences)
            }).ToArray();
        var meshes = snapshot.AttachmentMeshes.Select(candidate =>
            candidate with
            {
                Occurrences = candidate.Occurrences.Where(value => Keep(value.PackagePath, value.ExportUIndex)).ToArray()
            }).Where(candidate => candidate.Occurrences.Count > 0)
            .Select(candidate => candidate with
            {
                EffectiveOccurrence = ChooseMesh(candidate.Occurrences)
            }).ToArray();
        return snapshot with
        {
            Candidates = textures,
            AttachmentMeshes = meshes,
            ManualAssets = snapshot.ManualAssets.Except(replaced).ToArray()
        };
    }

    internal static TextureRegistrySnapshot Apply(
        MorphFaceGame game,
        TextureRegistrySnapshot baseSnapshot,
        IReadOnlyList<ManualRegistryAsset> selections,
        TextureRegistrySnapshot? previous,
        CancellationToken cancellationToken,
        out IReadOnlyList<string> failures)
    {
        var textures = baseSnapshot.Candidates.ToDictionary(
            candidate => candidate.InstancedPath, StringComparer.OrdinalIgnoreCase);
        var meshes = baseSnapshot.AttachmentMeshes.ToDictionary(
            candidate => candidate.CanonicalPath, StringComparer.OrdinalIgnoreCase);
        var retained = new List<ManualRegistryAsset>(selections.Count);
        var errors = new List<string>();

        foreach (var group in selections.GroupBy(asset => Path.GetFullPath(asset.PackagePath),
                     StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IMEPackage? package = null;
            try
            {
                if (!File.Exists(group.Key)) throw new FileNotFoundException("Package file was not found.", group.Key);
                LegendaryExplorerCoreRuntime.Initialize();
                package = MEPackageHandler.OpenMEPackage(group.Key, forceLoadFromDisk: true);
                if (package.Game != LecTextureRegistryPackageScanner.ToMeGame(game))
                    throw new InvalidDataException($"Package belongs to {package.Game}, not {game}.");
                foreach (var selected in group)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var export = package.Exports.FirstOrDefault(value =>
                            value.UIndex == selected.ExportUIndex &&
                            value.InstancedFullPath.Equals(selected.InstancedPath, StringComparison.OrdinalIgnoreCase) &&
                            value.ClassName.Equals(selected.ClassName, StringComparison.OrdinalIgnoreCase)) ??
                            package.Exports.SingleOrDefault(value =>
                                value.InstancedFullPath.Equals(selected.InstancedPath, StringComparison.OrdinalIgnoreCase) &&
                                value.ClassName.Equals(selected.ClassName, StringComparison.OrdinalIgnoreCase));
                        if (export is null || export.IsDefaultObject)
                        {
                            RetainMissing(selected, "Export was not found.");
                            continue;
                        }
                        var resolved = selected with
                        {
                            PackagePath = group.Key,
                            ExportUIndex = export.UIndex,
                            IsMissing = false
                        };
                        if (selected.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase))
                        {
                            var occurrence = LecTextureRegistryPackageScanner.ReadTextureOccurrence(
                                export, group.Key, 0, TextureCatalogOrigin.Manual);
                            var existing = textures.GetValueOrDefault(export.InstancedFullPath);
                            var occurrences = (existing?.Occurrences ?? [])
                                .Where(value => !SameSource(value.PackagePath, value.ExportUIndex, group.Key, export.UIndex))
                                .Append(occurrence).ToArray();
                            textures[export.InstancedFullPath] = new TextureCatalogCandidate(
                                ToCatalogGame(game), export.InstancedFullPath,
                                ChooseTexture(occurrences), occurrences);
                        }
                        else if (selected.ClassName.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase))
                        {
                            var canonical = PccAssetPathPolicy.FromDonorOccurrence(export.InstancedFullPath, group.Key);
                            var occurrence = new AttachmentMeshOccurrence(group.Key, export.InstancedFullPath,
                                export.UIndex, 0, TextureCatalogOrigin.Manual,
                                LecTextureRegistryPackageScanner.TryGetBoneCount(export));
                            var existing = meshes.GetValueOrDefault(canonical);
                            var occurrences = (existing?.Occurrences ?? [])
                                .Where(value => !SameSource(value.PackagePath, value.ExportUIndex, group.Key, export.UIndex))
                                .Append(occurrence).ToArray();
                            meshes[canonical] = new AttachmentMeshCandidate(canonical,
                                ChooseMesh(occurrences), occurrences);
                        }
                        else
                            throw new InvalidDataException($"Unsupported asset class '{selected.ClassName}'.");
                        retained.Add(resolved);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        RetainMissing(selected, exception.Message);
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                foreach (var selected in group)
                    RetainMissing(selected, exception.Message);
            }
            finally
            {
                package?.Dispose();
            }
        }

        failures = errors;
        return baseSnapshot with
        {
            Candidates = textures.Values.OrderBy(value => value.InstancedPath,
                StringComparer.OrdinalIgnoreCase).ToArray(),
            AttachmentMeshes = meshes.Values.OrderBy(value => value.CanonicalPath,
                StringComparer.OrdinalIgnoreCase).ToArray(),
            ManualAssets = retained.Concat(baseSnapshot.ManualAssets)
                .DistinctBy(Key, StringComparer.OrdinalIgnoreCase).ToArray()
        };

        void RetainMissing(ManualRegistryAsset selected, string reason)
        {
            errors.Add($"{selected.PackagePath} | {selected.ClassName} | {selected.InstancedPath}: {reason}");
            retained.Add(selected with { IsMissing = true });
            if (previous is null) return;
            if (selected.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase))
            {
                var old = previous.Candidates.FirstOrDefault(value =>
                    value.InstancedPath.Equals(selected.InstancedPath, StringComparison.OrdinalIgnoreCase));
                var occurrence = old?.Occurrences.FirstOrDefault(value =>
                    SameSource(value.PackagePath, value.ExportUIndex, selected.PackagePath, selected.ExportUIndex));
                if (occurrence is not null)
                {
                    var existing = textures.GetValueOrDefault(selected.InstancedPath);
                    var all = (existing?.Occurrences ?? []).Append(occurrence).Distinct().ToArray();
                    textures[selected.InstancedPath] = new TextureCatalogCandidate(ToCatalogGame(game),
                        selected.InstancedPath, ChooseTexture(all), all);
                }
            }
            else
            {
                var canonical = PccAssetPathPolicy.FromDonorOccurrence(selected.InstancedPath, selected.PackagePath);
                var old = previous.AttachmentMeshes.FirstOrDefault(value =>
                    value.CanonicalPath.Equals(canonical, StringComparison.OrdinalIgnoreCase));
                var occurrence = old?.Occurrences.FirstOrDefault(value =>
                    SameSource(value.PackagePath, value.ExportUIndex, selected.PackagePath, selected.ExportUIndex));
                if (occurrence is not null)
                {
                    var existing = meshes.GetValueOrDefault(canonical);
                    var all = (existing?.Occurrences ?? []).Append(occurrence).Distinct().ToArray();
                    meshes[canonical] = new AttachmentMeshCandidate(canonical, ChooseMesh(all), all);
                }
            }
        }
    }

    private static string Key(ManualRegistryAsset asset) =>
        $"{Path.GetFullPath(asset.PackagePath)}|{asset.ClassName}|{asset.InstancedPath}";

    private static string SourceKey(string path, int index) => $"{Path.GetFullPath(path)}|{index}";

    private static bool SameSource(string leftPath, int leftIndex, string rightPath, int rightIndex) =>
        leftIndex == rightIndex && Path.GetFullPath(leftPath).Equals(Path.GetFullPath(rightPath),
            StringComparison.OrdinalIgnoreCase);

    private static TextureCatalogOccurrence ChooseTexture(IReadOnlyList<TextureCatalogOccurrence> occurrences) =>
        occurrences.OrderByDescending(value => IsBiog(value.PackagePath))
            .ThenByDescending(value => value.Origin == TextureCatalogOrigin.Manual)
            .ThenByDescending(value => value.MountPriority).First();

    private static AttachmentMeshOccurrence ChooseMesh(IReadOnlyList<AttachmentMeshOccurrence> occurrences) =>
        occurrences.OrderByDescending(value => IsBiog(value.PackagePath))
            .ThenByDescending(value => value.Origin == TextureCatalogOrigin.Manual)
            .ThenByDescending(value => value.MountPriority).First();

    private static bool IsBiog(string path) =>
        Path.GetFileNameWithoutExtension(path).StartsWith("BIOG", StringComparison.OrdinalIgnoreCase);

    private static TextureCatalogGame ToCatalogGame(MorphFaceGame game) => game switch
    {
        MorphFaceGame.LE1 => TextureCatalogGame.LE1,
        MorphFaceGame.LE2 => TextureCatalogGame.LE2,
        MorphFaceGame.LE3 => TextureCatalogGame.LE3,
        _ => throw new ArgumentOutOfRangeException(nameof(game))
    };
}
