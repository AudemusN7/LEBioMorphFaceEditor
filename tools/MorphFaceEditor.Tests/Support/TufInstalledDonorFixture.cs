using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.LegendaryExplorer.TextureRegistry;

namespace MorphFaceEditor.Tests;

/// <summary>Feeds real local TUF packages through the normal installed registry, without changing an install.</summary>
internal sealed class TufInstalledDonorFixture : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"MFE-TUF-installed-{Guid.NewGuid():N}");

    internal TufInstalledDonorFixture(bool includeDonors = true)
    {
        if (includeDonors)
            foreach (var game in new[] { MorphFaceGame.LE1, MorphFaceGame.LE2, MorphFaceGame.LE3 }) PackagePath(game);
        Directory.CreateDirectory(_root);
        Store = new TextureRegistryStore(new TextureRegistryPaths(_root));
        foreach (var game in new[] { MorphFaceGame.LE1, MorphFaceGame.LE2, MorphFaceGame.LE3 })
        {
            var snapshot = new TextureRegistrySnapshot(TextureRegistrySnapshot.CurrentSchemaVersion,
                game, DateTimeOffset.UtcNow, 0, []);
            Store.WriteAtomic(snapshot);
            if (!includeDonors) continue;
            var packagePath = PackagePath(game);
            // An ordinary NPC is sufficient: New NPC resets its shape to the base.
            var donor = MorphFaceReferenceInspector.Inspect(packagePath).First(face =>
                !face.FacePath.EndsWith(".TUF_Authored_Neutral", StringComparison.OrdinalIgnoreCase));
            Store.WriteModsAtomic(snapshot with
            {
                InstalledPackageCount = 1,
                MountedPackagePaths = [packagePath],
                MorphFaceTemplates = [new MorphFaceTemplateCandidate(packagePath, donor.FaceUIndex,
                    donor.FacePath, donor.BaseHeadPath, 9000, TextureCatalogOrigin.Mod)]
            });
        }
        Catalog = new TextureCatalogService(Store);
    }

    internal TextureRegistryStore Store { get; }
    internal TextureCatalogService Catalog { get; }

    internal static string PackagePath(MorphFaceGame game)
    {
        var path = PackageTestFixtures.FixturePath(Path.Combine("TUF", $"{game} TUF.pcc"));
        if (!File.Exists(path)) throw new TestSkippedException($"Missing local TUF fixture: {path}");
        return path;
    }

    internal static string CreateMismatchedBakeCopy()
    {
        var source = PackagePath(MorphFaceGame.LE3);
        var copy = Path.Combine(Path.GetTempPath(), $"MFE-TUF-legacy-{Guid.NewGuid():N}.pcc");
        File.Copy(source, copy);
        try
        {
            using var package = MEPackageHandler.OpenMEPackage(copy, forceLoadFromDisk: true);
            var face = package.Exports.Single(e => e.ClassName == "BioMorphFace" && e.ObjectNameString == "TUF_Authored_Neutral");
            var binary = face.GetBinaryData<BioMorphFace>();
            binary.LODs[0] = binary.LODs[0][..^1];
            face.WriteBinary(binary);
            package.Save();
            return copy;
        }
        catch { File.Delete(copy); throw; }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
