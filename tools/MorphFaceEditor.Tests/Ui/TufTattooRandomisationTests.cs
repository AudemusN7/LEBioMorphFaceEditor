using System.Numerics;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Core.Randomisation;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;
using MorphFaceEditor.ViewModels;
using static MorphFaceEditor.Tests.UiTestFixtures;

namespace MorphFaceEditor.Tests;

internal static class TufTattooRandomisationTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
        [new("TUF tattoo randomisation uses workspace and installed donors", TattooDonors)];

    private static void TattooDonors() => WpfTestDispatcher.Invoke(() => RunWithDispatcher(async () =>
    {
        const MorphFaceGame game = MorphFaceGame.LE3;
        const string parameter = "TUR_HED_Tatt";
        string[] patterns = ["BIOG_TUF_HED_PROMorph_R.Tattoo.TUF_HED_PRO_Tat1", "BIOG_TUF_HED_PROMorph_R.Tattoo.TUF_HED_PRO_Tat2"];
        using var installed = new TufInstalledDonorFixture();
        var profiles = MorphFaceProfileRegistry.CreateDefault();
        var profile = profiles.Profiles.Single(p => p.Key == "le3-female-turian");
        var sources = await installed.Catalog.ReadAsync(game);
        var donor = new RonNpcDonorResolver(profiles).Resolve(game, "TUF", sources.MorphFaceTemplates, sources.OfficialPackagePaths);
        var entries = PackageAssetInspector.Inventory(donor.PackagePath, ["Texture2D"]).Entries
            .Where(entry => !entry.IsDefaultObject).ToDictionary(entry => entry.InstancedPath, StringComparer.OrdinalIgnoreCase);
        var occurrence = new TextureCatalogOccurrence(donor.PackagePath, entries[patterns[1]].UIndex, 9000,
            TextureCatalogOrigin.Mod, 1, 1, "PF_DXT1", "Character", false, null);
        var candidate = new TextureCatalogCandidate(game, patterns[1], occurrence, [occurrence]);
        var workspace = Path.Combine(Path.GetTempPath(), $"MFE-TUF-Tattoo-{Guid.NewGuid():N}.pcc");
        var thin = workspace + ".thin.pcc";
        var saved = workspace + ".saved.pcc";
        File.Copy(donor.PackagePath, workspace);
        try
        {
            using var reader = new MorphFacePackageReader();
            using var shell = CreateMainWindowViewModel(reader, textureCatalog: installed.Catalog);
            await shell.OpenDroppedFileAsync(workspace);
            shell.SelectedFace = shell.Faces.Single(face => face.ObjectName == "TUF_Authored_Neutral");
            await ((AsyncRelayCommand)shell.LoadSelectedFaceCommand).ExecuteAsync();
            var editor = shell.Editor ?? throw new Exception(shell.ErrorMessage ?? "Failed to load TUF face.");
            var tattoo = editor.Material.Textures.Single(t => editor.Material.SourceParameterName(t.Name)
                .Equals(parameter, StringComparison.OrdinalIgnoreCase));
            editor.Material.UpdateRegistryCandidates([candidate], TextureCatalogProfiles.For(profile), true);
            var available = MorphRandomisationCatalog.LoadEmbedded()
                .ProjectInstalledMaterialDonors(profile.Key, editor.Material.CanResolveRandomisationTexturePath)
                .SelectMany(d => d.MaterialTextureFamilies.Values).SelectMany(f => f)
                .Where(p => p.Key.Equals(parameter, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
            TestAssert.True(patterns.All(available.Contains), "Both tattoo patterns must be eligible for randomisation.");
            foreach (var pattern in patterns)
            {
                var prepared = await Prepare(editor.Material, tattoo.Name, parameter, pattern);
                TestAssert.Equal(1, prepared.AppliedTextureFamilies);
                editor.Material.ApplyRandomisation(prepared);
                var result = editor.Material.GetEffectiveTextureReference(tattoo.Name);
                TestAssert.True(result is not null && result.InstancedPath.Equals(pattern, StringComparison.OrdinalIgnoreCase),
                    "The approved tattoo pattern was not applied.");
                TestAssert.Equal(Path.GetFullPath(editor.CreateDraft().Source.PackagePath), Path.GetFullPath(result!.PackagePath));
            }

            // A new NPC package omits unused textures, so pattern 2 must come from an installed donor.
            var native = reader.Load(donor.PackagePath, donor.FacePath, decodeTextures: false);
            using var created = new StandaloneNpcMorphImportService().ImportNpcHead(new StandaloneNpcMorphImportRequest(
                game, game, "TUF", MorphFacePackageContextService.CreateDefaultHead(native), donor.PackagePath,
                donor.FacePath, "MFE_TUF_NewTexture", IsVerifiedNativeNpcDonor: true));
            var loaded = reader.Load(created.Workspace.WorkingPath, created.ImportedFacePath, decodeTextures: false);
            new MorphFacePackageWriter().SaveMorphToPackage(loaded.Document, created.Workspace.WorkingPath, thin, true);
            TestAssert.True(!PackageAssetInspector.Inventory(thin, ["Texture2D"]).Entries.Any(e =>
                e.InstancedPath.Equals(patterns[1], StringComparison.OrdinalIgnoreCase)), "Pattern 2 must be absent from the new PCC.");
            using var secondReader = new MorphFacePackageReader();
            using var secondShell = CreateMainWindowViewModel(secondReader, textureCatalog: installed.Catalog);
            await secondShell.OpenDroppedFileAsync(thin);
            secondShell.SelectedFace = secondShell.Faces.Single(face => face.ObjectName == "MFE_TUF_NewTexture");
            await ((AsyncRelayCommand)secondShell.LoadSelectedFaceCommand).ExecuteAsync();
            editor = secondShell.Editor ?? throw new Exception(secondShell.ErrorMessage ?? "Failed to load new TUF face.");
            tattoo = editor.Material.Textures.Single(t => editor.Material.SourceParameterName(t.Name)
                .Equals(parameter, StringComparison.OrdinalIgnoreCase));
            editor.Material.UpdateRegistryCandidates([], TextureCatalogProfiles.For(profile), true);
            TestAssert.True(!tattoo.CanResolveRandomisationPath(patterns[1]), "A missing tattoo resolved without an installed donor.");
            installed.Store.WriteModsAtomic(installed.Store.ReadMods(game) with { Candidates = [candidate] });
            secondShell.RefreshTextureRegistry(game);
            for (var attempt = 0; attempt < 100 && !tattoo.CanResolveRandomisationPath(patterns[1]); attempt++)
                await Task.Delay(20);
            var decoded = await tattoo.ResolveRandomisationPathAsync(patterns[1]);
            TestAssert.Equal(donor.PackagePath, decoded.Source.PackagePath);
            TestAssert.True(decoded.Source.IsExplicitTextureDonor, "The installed tattoo lost its physical donor identity.");
            var installedRoll = await Prepare(editor.Material, tattoo.Name, parameter, patterns[1]);
            TestAssert.Equal(1, installedRoll.AppliedTextureFamilies);
            editor.Material.ApplyRandomisation(installedRoll);
            var chosen = editor.Material.GetEffectiveTextureReference(tattoo.Name);
            TestAssert.True(chosen is not null && chosen.IsExplicitTextureDonor && chosen.PackagePath == donor.PackagePath,
                "The applied tattoo lost its installed donor identity.");
            var draft = editor.CreateDraft();
            var exported = new MorphFacePackageWriter().SaveMorphToPackage(draft, draft.Source.PackagePath, saved, true);
            var reopened = secondReader.Load(exported.PackagePath, exported.FaceInstancedPath);
            TestAssert.True(reopened.Materials.Materials.Values.SelectMany(material => material.Textures.Values)
                .Any(binding => binding.Texture.Source.InstancedPath.Equals(patterns[1], StringComparison.OrdinalIgnoreCase) &&
                    binding.Texture.Rgba8.SequenceEqual(decoded.Rgba8)), "Export changed the installed tattoo or its pixels.");
        }
        finally
        {
            foreach (var path in new[] { workspace, thin, saved })
                if (File.Exists(path)) File.Delete(path);
        }
    }));

    private static Task<PreparedMaterialRandomisation> Prepare(MaterialEditorViewModel editor, string control, string parameter, string path) =>
        editor.PrepareRandomisationAsync(new Dictionary<string, float>(), new Dictionary<string, Vector4>(),
            new Dictionary<string, IReadOnlyDictionary<string, string>> { ["tattoo"] = new Dictionary<string, string> { [parameter] = path } },
            editor.ParameterScopeKey(control));
}
