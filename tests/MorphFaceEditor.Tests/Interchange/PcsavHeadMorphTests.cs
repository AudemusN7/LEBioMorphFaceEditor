using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Core.Services;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.Tests;

internal static class PcsavHeadMorphTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("PCSAV rejects corrupt saves and protects externally changed sources", FailedSavesPreserveSources),
        new("PCSAV Save As preserves original and redirects subsequent saves", SaveAsCopies),
        new("PCSAV changing hair clears hidden hair textures but preserves scalp and unchanged hair", HairTextureClearing),
        new("PCSAV rejects new unavailable assets while preserving untouched native references", RuntimeAssetChanges),
        new("PCC Save As preserves original and redirects commits", PackageSaveAsCopies),
        new("Morphs-only RON round-trips features without weakening full head validation", MorphFeaturesRonRoundTrip)
    ];

    internal static string[] Fixtures()
    {
        var directory = FindRepositoryDirectory();
        var fixtureDirectory = directory is null ? null : Path.Combine(directory, "fixtures", "PCSAV_Test");
        if (fixtureDirectory is null || !Directory.Exists(fixtureDirectory))
            throw new TestSkippedException("Local PCSAV career fixtures are not present.");
        // QA owns the career matrix. Failure checks need one native format per game.
        return new[] { "LE1-Male-Custom.pcsav", "LE2-Female-Custom.pcsav", "LE3-Male-Custom.pcsav" }
            .Select(name => Path.Combine(fixtureDirectory, name)).ToArray();
    }

    private static string? FindRepositoryDirectory()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "fixtures", "PCSAV_Test")))
                return current.FullName;
            current = current.Parent;
        }
        return null;
    }

    private static void FailedSavesPreserveSources()
    {
        foreach (var fixture in Fixtures())
        {
            var directory = Path.Combine(Path.GetTempPath(), $"MFE-PcsavFailure-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                var source = Path.Combine(directory, "career.pcsav"); File.Copy(fixture, source);
                var workspace = PcsavHeadMorphWorkspace.Open(source);
                var head = workspace.Head; workspace.SetEditingBaseline(head);
                File.SetLastWriteTimeUtc(source, File.GetLastWriteTimeUtc(source).AddMinutes(1));
                workspace.SaveEditedHead(head);
                var corrupted = File.ReadAllBytes(source); corrupted[20] ^= 0x80; File.WriteAllBytes(source, corrupted);
                try { _ = PcsavHeadMorphWorkspace.Open(source); throw new Exception("A corrupt save was accepted."); }
                catch (PcsavException e) { TestAssert.Equal(PcsavError.InvalidFile, e.Reason); }
                try { workspace.SaveEditedHead(head); throw new Exception("An externally changed career was overwritten."); }
                catch (PcsavException e) { TestAssert.Equal(PcsavError.SourceChanged, e.Reason); }
                TestAssert.True(corrupted.SequenceEqual(File.ReadAllBytes(source)), "A failed save changed its source.");
                TestAssert.Equal(0, Directory.GetFiles(directory, ".*.tmp").Length);
            }
            finally { Directory.Delete(directory, recursive: true); }
        }
    }

    private static void SaveAsCopies()
    {
        foreach (var fixture in Fixtures().Where(path => Path.GetFileName(path) == "LE2-Female-Custom.pcsav"))
        {
            var directory = Path.Combine(Path.GetTempPath(), $"MFE-PcsavAs-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                var source = Path.Combine(directory, "original.pcsav"); File.Copy(fixture, source);
                var original = File.ReadAllBytes(source);
                var workspace = PcsavHeadMorphWorkspace.Open(source);
                var head = workspace.Head; workspace.SetEditingBaseline(head);
                var copy = Path.Combine(directory, "copy.pcsav");
                foreach (var existing in new[] { false, true })
                {
                    if (existing) File.Copy(source, copy, overwrite: true);
                    FileSaveTransaction.BeforeCommitForTesting = (_, target) => File.WriteAllText(target, "outside edit");
                    try { workspace.SaveEditedHead(head, copy); throw new Exception("Save As overwrote an outside edit."); }
                    catch (PcsavException exception) { TestAssert.Equal(PcsavError.SourceChanged, exception.Reason); }
                    TestAssert.Equal(source, workspace.SourcePath);
                    TestAssert.Equal("outside edit", File.ReadAllText(copy));
                    TestAssert.Equal(0, Directory.GetFiles(directory, ".*.tmp").Length);
                }
                FileSaveTransaction.BeforeCommitForTesting = null;
                workspace.SaveEditedHead(head, copy);
                TestAssert.Equal(copy, workspace.SourcePath);
                copy += ".new.pcsav";
                workspace.SaveEditedHead(head, copy);
                TestAssert.Equal(copy, workspace.SourcePath);
                var saved = PcsavHeadMorphWorkspace.Open(copy);
                TestAssert.True(saved.HasHeadMorph && original.SequenceEqual(File.ReadAllBytes(source)), "Save As modified its original.");
                workspace.DeleteHeadMorph();
                TestAssert.True(!PcsavHeadMorphWorkspace.Open(copy).HasHeadMorph && original.SequenceEqual(File.ReadAllBytes(source)),
                    "Saving after Save As targeted the original.");
                TestAssert.Equal(0, Directory.GetFiles(directory, "*.backup").Length);
            }
            finally { FileSaveTransaction.BeforeCommitForTesting = null; Directory.Delete(directory, true); }
        }
    }

    private static void RuntimeAssetChanges()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"MFE-PcsavRuntime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "career.pcsav");
            File.Copy(Fixtures().First(path => Path.GetFileName(path) == "LE2-Female-Custom.pcsav"), source);
            var native = PcsavHeadMorphWorkspace.Open(source);
            var missingRoot = "BIOG_MFE_Missing_" + Guid.NewGuid().ToString("N");
            var unknown = new TextureMaterialOverride("NativeUnavailable", new("", missingRoot + ".OldTexture", 0, "Texture2D"));
            var head = native.Head with { HairMesh = missingRoot + ".OldHair", MaterialData = native.Head.MaterialData with
                { Textures = native.Head.MaterialData.Textures.Append(unknown).ToArray() } };
            File.WriteAllBytes(source, native.ReplaceHead(head));
            var workspace = PcsavHeadMorphWorkspace.Open(source);
            var projection = head with { HairMesh = "None", MaterialData = head.MaterialData with
                { Textures = head.MaterialData.Textures.Where(value => value != unknown).ToArray() } };
            workspace.SetEditingBaseline(projection);
            var scalar = projection.MaterialData.Scalars.First();
            projection = projection with { MaterialData = projection.MaterialData with { Scalars = projection.MaterialData.Scalars
                .Select(value => value.Name == scalar.Name ? value with { Value = value.Value + 0.125f } : value).ToArray() } };
            workspace.SaveEditedHead(projection);
            var saved = PcsavHeadMorphWorkspace.Open(source);
            TestAssert.Equal(head.HairMesh, saved.Head.HairMesh);
            TestAssert.Equal(unknown.TextureReference!.InstancedPath, saved.Head.MaterialData.Textures
                .Single(value => value.Name == unknown.Name).TextureReference!.InstancedPath);
            TestAssert.True(workspace.UnrelatedPrefix.SequenceEqual(saved.UnrelatedPrefix) &&
                workspace.UnrelatedSuffix.SequenceEqual(saved.UnrelatedSuffix), "An unrelated edit damaged native career data.");
            var before = File.ReadAllBytes(source);
            var selected = projection.MaterialData.Textures.First();
            var changed = projection with { MaterialData = projection.MaterialData with { Textures = projection.MaterialData.Textures
                .Select(value => value == selected ? value with { TextureReference = new("", missingRoot + ".NewTexture", 0, "Texture2D") } : value)
                .ToArray() } };
            var destination = Path.Combine(directory, "existing.pcsav");
            File.WriteAllBytes(destination, before);
            foreach (var target in new[] { source, destination })
            {
                try { workspace.SaveEditedHead(changed, target); throw new Exception("An unavailable new texture reached the career save."); }
                catch (InvalidDataException exception)
                {
                    TestAssert.Equal("loc_PlayerAsset_SeekFreeSourceRequired", UserFacingMessage.FromException(exception)!.LocalizationKey);
                }
                TestAssert.True(before.SequenceEqual(File.ReadAllBytes(source)) && before.SequenceEqual(File.ReadAllBytes(destination)),
                    "A rejected career save overwrote its source or destination.");
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void HairTextureClearing()
    {
        var original = new HeadMorphPayload("Test.OldHair", [], new([], [], []), new([], [], []));
        var hair = new TextureMaterialOverride("HAIR_DIFF", TestFixtures.CreateIdentity("Test.Hair", "Texture2D"));
        var scalp = hair with { Name = "HED_Scalp_Diff" };
        original = original with { MaterialData = original.MaterialData with { Textures = [hair, scalp] } };
        var baseline = original with { MaterialData = original.MaterialData with { Textures = [scalp] } };
        var unchanged = PcsavHeadMorphMerge.ApplyChanges(original, baseline, baseline);
        TestAssert.True(unchanged.MaterialData.Textures.Any(v => v.Name == hair.Name), "Unchanged hair lost native data.");
        var changed = PcsavHeadMorphMerge.ApplyChanges(original, baseline, baseline with { HairMesh = "Test.NewHair" });
        TestAssert.True(!changed.MaterialData.Textures.Any(v => v.Name == hair.Name) && changed.MaterialData.Textures.Any(v => v.Name == scalp.Name),
            "Changing hair did not clear its map while preserving scalp maps.");
    }

    private static void PackageSaveAsCopies()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"MFE-PccAs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "Original.pcc");
            File.Copy(StandalonePlayerMorphImportService.ResolveInstalledSeed(MorphFaceGame.LE1), source);
            var original = File.ReadAllBytes(source);
            using var workspace = new MorphFacePackageWorkspace(source);
            var copy = Path.Combine(directory, "Copy.pcc");
            workspace.Commit(copy);
            TestAssert.Equal(copy, workspace.SourcePath);
            TestAssert.True(original.SequenceEqual(File.ReadAllBytes(source)) && original.SequenceEqual(File.ReadAllBytes(copy)),
                "PCC Save As modified the original or omitted data.");
            var face = StandalonePlayerMorphImportService.ResolvePlayerTemplate(MorphFaceGame.LE1, StandalonePlayerSex.Male);
            var service = new MorphFacePackageContextService();
            var data = service.CaptureMaterialData(workspace.WorkingPath, face);
            var first = data.Scalars.FirstOrDefault() ?? new ScalarMaterialOverride("HED_Norm_Blend", 0);
            service.PasteMaterialData(workspace.WorkingPath, face, data with { Scalars = data.Scalars
                .Where(v => v.Name != first.Name).Append(first with { Value = first.Value + 0.125f }).ToArray() });
            workspace.Commit();
            TestAssert.True(original.SequenceEqual(File.ReadAllBytes(source)), "PCC commit after Save As overwrote the original.");
            TestAssert.Near(first.Value + 0.125f, service.CaptureMaterialData(copy, face).Scalars.First(v => v.Name == first.Name).Value, 0.00001f);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void MorphFeaturesRonRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"MFE-Features-{Guid.NewGuid():N}.ron");
        try
        {
            var head = new HeadMorphPayload("None", [], new([new("nose_BridgeIn", 0.25f)], [], []), new([], [], []));
            var metadata = new RonExportProvenance(RonExportProducer.MFE, MorphFaceGame.LE3, "HMM", true);
            TseHeadMorphRon.Write(path, head, metadata, morphFeaturesOnly: true);
            var read = TseHeadMorphRon.Read(path);
            TestAssert.True(head.MorphData.MorphFeatures.SequenceEqual(read.MorphData.MorphFeatures), "Features did not round-trip.");
            TestAssert.Equal(metadata, TseHeadMorphRon.ReadProvenance(path));
            try
            {
                using var imported = new StandalonePlayerMorphImportService().ImportPlayerRon(MorphFaceGame.LE3, path, "RejectedPartial");
                throw new Exception("The full-head importer accepted a features-only file.");
            }
            catch (InvalidDataException exception)
            {
                TestAssert.True(exception.Message.Contains("features only"), "The full-head importer did not explain the missing geometry.");
            }
            var text = File.ReadAllText(path);
            File.WriteAllText(path, text.Replace(MfeRonMetadata.Morphs, MfeRonMetadata.Head));
            try { TseHeadMorphRon.Validate(path); throw new Exception("An empty full head was accepted."); }
            catch (InvalidDataException) { }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
