using System.Numerics;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Services;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.QA;

// Disposable, deterministic test inputs. These are generated from installed seeds,
// not distributed game or user-authored geometry.
internal sealed class QaSyntheticPlayerBaselines : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"MFE-QA-Baselines-{Guid.NewGuid():N}");
    public PlayerMorphBaselineCatalog Catalog { get; }
    internal string Root => _root;

    public QaSyntheticPlayerBaselines(IEnumerable<MorphFaceGame> games, IEnumerable<StandalonePlayerSex> sexes)
    {
        try
        {
            Directory.CreateDirectory(_root);
            using var reader = new MorphFacePackageReader();
            var profiles = MorphFaceProfileRegistry.CreateDefault();
            var targetCatalog = new MorphTargetCatalog();
            foreach (var game in games.Distinct())
            foreach (var sex in sexes.Distinct())
            {
                var seed = reader.Load(StandalonePlayerMorphImportService.ResolveInstalledSeed(game),
                    StandalonePlayerMorphImportService.ResolvePlayerTemplate(game, sex), decodeTextures: false);
                var head = MorphFacePackageContextService.CreateDefaultHead(seed);
                var profile = profiles.Require(game,
                    seed.Document.Source.InstancedPath, seed.Document.BaseHeadReference?.InstancedPath);
                var targets = targetCatalog.Load(profile);
                var document = seed.Document with { MorphFeatures = head.MorphData.MorphFeatures,
                    FinalSkeleton = head.MorphData.FinalSkeleton, BakedLods = head.MorphData.BakedLods };
                var session = new MorphFaceEditingSession(document, seed.BaseHead, targets,
                    profile.MetadataOnlyFeatures, profile.Key, profile.FeatureAliases, geometryMode: MorphFaceGeometryMode.RelativeBake);
                var nativeNames = targets.Where(target => target.Lods.Any(lod => lod.LodIndex == 0 && lod.Vertices.Count > 0))
                    .Select(target => target.Source.InstancedPath.Split('.').Last())
                    .Where(name => !profile.MetadataOnlyFeatures.Contains(name) && !profile.FeatureAliases.ContainsKey(name))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var feature = session.Features.First(value => nativeNames.Contains(value.Name));
                session.SetFeature(feature.Name, 0.05f + (int)game * 0.01f);
                var bone = session.Evaluation.FinalSkeleton.First(value =>
                    value.BoneName.Contains("jaw", StringComparison.OrdinalIgnoreCase));
                session.SetBoneAxis(bone.BoneName, 0, session.GetBoneAxis(bone.BoneName, 0) + 0.006f + (int)game * 0.001f);
                var draft = session.CreateDraft(document.HairMeshReference, document.OtherMeshReferences, document.MaterialOverrides);
                var lods = draft.BakedLods.Select(lod => lod.ToArray()).ToArray();
                if (lods.Length == 0 || lods[0].Length < 3)
                    throw new InvalidDataException($"Installed {game} {sex} seed has insufficient baked geometry.");
                // Deliberately independent of target sliders, different for each game/sex.
                float scale = (int)game * 0.001f + (sex == StandalonePlayerSex.Female ? 0.007f : 0.011f);
                lods[0][0] += new Vector3(scale, 0.021f, -0.013f);
                lods[0][1] += new Vector3(-0.019f, scale, 0.017f);
                lods[0][2] += new Vector3(0.014f, -0.023f, scale);
                head = head with { MorphData = new(draft.MorphFeatures, draft.FinalSkeleton, lods) };
                var directory = Path.Combine(_root, "synthetic", sex == StandalonePlayerSex.Female ? "human-female" : "human-male");
                Directory.CreateDirectory(directory);
                TseHeadMorphRon.Write(Path.Combine(directory, game + ".ron"), head);
            }
            Catalog = new PlayerMorphBaselineCatalog(_root);
        }
        catch { Dispose(); throw; }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
