using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.Classes;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.Tests;

internal static class TextureTransferDonorPolicyTests
{
    private const string TexturePath = "BIOG_Test.Add.ASA_HED_PRO_Add2";

    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("texture transfer uses the eligible official donor over a higher-mounted mod", OfficialBeatsMod),
        new("texture transfer does not automatically select a mod-only catalog occurrence", ModOnlyIsNotAutomatic),
        new("allowlisted texture transfer preserves an explicitly selected source donor", ExplicitSourceWins)
    ];

    private static void OfficialBeatsMod()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        using var donors = new TempDonors();
        var official = donors.Create("BIOG_Official", 31, MEGame.LE3);
        var mod = donors.Create("BIOG_Mod", 207, MEGame.LE3);
        var officialOccurrence = Occurrence(official.PackagePath, official.Entry.UIndex, 0, TextureCatalogOrigin.BaseGame);
        var modOccurrence = Occurrence(mod.PackagePath, mod.Entry.UIndex, 1000, TextureCatalogOrigin.Mod);
        var candidate = new TextureCatalogCandidate(
            TextureCatalogGame.LE3,
            TexturePath,
            officialOccurrence,
            [officialOccurrence, modOccurrence]) { IsEligibleOfficialDefault = true };

        using var destination = MEPackageHandler.CreateMemoryEmptyPackage("TransferTarget.pcc", MEGame.LE3);
        var result = MorphFaceTextureTransferEngine.Transfer(
            destination,
            Material(TexturePath),
            new HashSet<string>(), new HashSet<string>(), new HashSet<string>(["Add2"]),
            MEGame.LE2, "le2-asari", string.Empty, null, [], [candidate], EmptyResolver());

        var imported = destination.FindExport(TexturePath, "Texture2D");
        TestAssert.True(imported is not null, "The eligible official texture was not materialized.");
        TestAssert.Equal((byte)31, new Texture2D(imported!).GetTopMip().Mip[0]);
        TestAssert.Equal(TexturePath, result.MaterialData.Textures.Single().TextureReference!.InstancedPath);
    }

    private static void ModOnlyIsNotAutomatic()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        using var donors = new TempDonors();
        var mod = donors.Create("BIOG_ModOnly", 207, MEGame.LE3);
        var occurrence = Occurrence(mod.PackagePath, mod.Entry.UIndex, 1000, TextureCatalogOrigin.Mod);
        var candidate = new TextureCatalogCandidate(
            TextureCatalogGame.LE3,
            TexturePath,
            occurrence,
            [occurrence]) { IsEligibleOfficialDefault = false };

        using var destination = MEPackageHandler.CreateMemoryEmptyPackage("TransferModOnly.pcc", MEGame.LE3);
        var result = MorphFaceTextureTransferEngine.Transfer(
            destination,
            Material(TexturePath),
            new HashSet<string>(), new HashSet<string>(), new HashSet<string>(["Add2"]),
            MEGame.LE2, "le2-asari", string.Empty, null, [], [candidate], EmptyResolver());

        TestAssert.True(destination.FindExport(TexturePath, "Texture2D") is null,
            "A mod-only catalog candidate was automatically materialized.");
        TestAssert.True(result.Warnings.Any(warning => warning.Contains(TexturePath, StringComparison.OrdinalIgnoreCase)),
            "The missing automatic donor did not produce a conversion warning.");
    }

    private static void ExplicitSourceWins()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        using var donors = new TempDonors();
        var official = donors.Create("BIOG_Test", 31, MEGame.LE2);
        var mod = donors.Create("BIOG_ExplicitMod", 207, MEGame.LE2);
        var resolver = ResolverFor(MEGame.LE2, "BIOG_Test.pcc", official.PackagePath);
        using var destination = MEPackageHandler.CreateMemoryEmptyPackage("TransferExplicitSource.pcc", MEGame.LE1);
        var explicitIdentity = new AssetIdentity(
            mod.PackagePath, TexturePath, mod.Entry.UIndex, "Texture2D") { IsExplicitTextureDonor = true };
        var source = new MorphFaceMaterialData([], [], [new TextureMaterialOverride("Add2", explicitIdentity)]);

        var result = MorphFaceTextureTransferEngine.Transfer(
            destination,
            source,
            new HashSet<string>(), new HashSet<string>(), new HashSet<string>(["Add2"]),
            MEGame.LE2, "le2-asari", official.PackagePath, null, [], [], resolver);

        var embedded = destination.FindExport(TexturePath, "Texture2D");
        TestAssert.True(embedded is not null, "The explicitly selected source texture was not embedded.");
        TestAssert.Equal((byte)207, new Texture2D(embedded!).GetTopMip().Mip[0]);
        TestAssert.True(new Texture2D(embedded!).GetTopMip().IsPackageStored,
            "The selected source texture was not converted to package storage.");
        TestAssert.Equal(Path.GetFullPath(destination.FilePath), result.MaterialData.Textures.Single().TextureReference!.PackagePath);
    }

    private static MorphFaceMaterialData Material(string path) => new(
        [], [], [new TextureMaterialOverride("Add2", new AssetIdentity(string.Empty, path, 0, "Texture2D"))]);

    private static InstalledGameAssetResolver EmptyResolver() => new(
        loadedFiles: _ => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        snapshot: _ => null);

    private static InstalledGameAssetResolver ResolverFor(MEGame game, string filename, string path) => new(
        loadedFiles: requestedGame => requestedGame == game
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [filename] = path }
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        snapshot: _ => null);

    private static TextureCatalogOccurrence Occurrence(
        string path,
        int uIndex,
        int mountPriority,
        TextureCatalogOrigin origin) => new(
        path, uIndex, mountPriority, origin, 1, 1, "PF_A8R8G8B8", "TEXTUREGROUP_UI", false, null);

    private sealed class TempDonors : IDisposable
    {
        private readonly List<string> _paths = [];

        internal (string PackagePath, ExportEntry Entry) Create(string packageName, byte marker, MEGame game)
        {
            var path = Path.Combine(Path.GetTempPath(), $"{packageName}-{Guid.NewGuid():N}.pcc");
            using (var package = MEPackageHandler.CreateMemoryEmptyPackage(Path.GetFileName(path), game))
            {
                var root = package.CreatePackageExport("BIOG_Test");
                var group = package.CreatePackageExport("Add", root);
                var texture = package.CreateExport("ASA_HED_PRO_Add2", "Texture2D", group, indexed: false);
                texture.WriteProperty(new EnumProperty(
                    new NameReference("PF_A8R8G8B8"), new NameReference("EPixelFormat"), game, "Format"));
                texture.WriteBinary(new UTexture2D
                {
                    Mips = [new UTexture2D.Texture2DMipMap([marker, 0, 0, 255], 1, 1)]
                });
                package.Save(path);
            }
            _paths.Add(path);
            using var reopened = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
            return (path, reopened.FindExport(TexturePath, "Texture2D") ??
                throw new InvalidDataException($"Synthetic donor did not contain {TexturePath}."));
        }

        public void Dispose()
        {
            foreach (var path in _paths)
            {
                try { File.Delete(path); }
                catch { }
            }
        }
    }
}
