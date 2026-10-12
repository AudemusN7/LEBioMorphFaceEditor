using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.Tests;

internal static class PcsavHeadMorphTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("PCSAV custom and default careers round-trip and preserve unrelated bytes when edited", CareerRoundTrips),
        new("PCSAV rejects corrupt saves and protects externally changed sources", FailedSavesPreserveSources),
        new("PCSAV Save As preserves original and redirects subsequent saves", SaveAsCopies),
        new("PCSAV changing hair clears hidden hair textures but preserves scalp and unchanged hair", HairTextureClearing),
        new("PCC Save As preserves original and redirects commits", PackageSaveAsCopies),
        new("Morphs-only RON round-trips features without weakening full head validation", MorphFeaturesRonRoundTrip)
    ];

    internal static string[] Fixtures()
    {
        var directory = FindRepositoryDirectory();
        var fixtureDirectory = directory is null ? null : Path.Combine(directory, "fixtures", "PCSAV_Test");
        if (fixtureDirectory is null || !Directory.Exists(fixtureDirectory))
            throw new TestSkippedException("Local PCSAV career fixtures are not present.");
        var paths = Directory.GetFiles(fixtureDirectory, "*.pcsav").Order().ToArray();
        var names = paths.Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "LE1-Male-Custom", "LE1-Female-Custom", "LE1-Default",
            "LE2-Male-Custom", "LE2-Female-Custom", "LE2-Default",
            "LE3-Male-Custom", "LE3-Female-Custom", "LE3-Default"
        };
        TestAssert.True(expected.SetEquals(names),
            $"PCSAV fixture inventory must contain custom male/female and default saves for LE1, LE2, and LE3; found {paths.Length} files.");
        return paths;
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

    private static void CareerRoundTrips()
    {
        var customObserved = new HashSet<(MorphFaceGame, StandalonePlayerSex)>();
        var defaultsObserved = new HashSet<MorphFaceGame>();
        foreach (var fixture in Fixtures())
        {
            var directory = Path.Combine(Path.GetTempPath(), $"MFE-Pcsav-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                var source = Path.Combine(directory, Path.GetFileName(fixture)); File.Copy(fixture, source);
                var original = File.ReadAllBytes(source);
                var workspace = PcsavHeadMorphWorkspace.Open(source);
                var isCustom = Path.GetFileNameWithoutExtension(fixture).EndsWith("-Custom", StringComparison.OrdinalIgnoreCase);
                TestAssert.True(isCustom == workspace.HasHeadMorph,
                    $"{Path.GetFileName(fixture)} does not match its custom/default fixture label.");
                if (isCustom)
                    customObserved.Add((workspace.Game, workspace.Sex));
                else
                    defaultsObserved.Add(workspace.Game);
                Console.WriteLine($"PCSAV {Path.GetFileName(fixture)}: {workspace.Game} {workspace.Sex}, {workspace.FirstName}, " +
                    (workspace.HasHeadMorph ? $"{workspace.Head.MorphData.BakedLods[0].Length} LOD0 vertices" : "default head"));
                var ron = Path.Combine(directory, "head.ron");
                if (!workspace.HasHeadMorph)
                {
                    TestAssert.True(original.SequenceEqual(workspace.ReplaceHead(null)), "An untouched default-head career changed bytes.");
                    ExportEditableHead(workspace, ron); workspace.SetEditingBaseline(ron); workspace.SaveEditedRon(ron);
                    var created = PcsavHeadMorphWorkspace.Open(source);
                    TestAssert.True(created.HasHeadMorph, "A default-head career did not accept a new custom head.");
                    TestAssert.True(workspace.UnrelatedPrefix.SequenceEqual(created.UnrelatedPrefix) &&
                        workspace.UnrelatedSuffix.SequenceEqual(created.UnrelatedSuffix), "Creating a head changed unrelated data.");
                    workspace.DeleteHeadMorph();
                    TestAssert.True(original.SequenceEqual(File.ReadAllBytes(source)), "Deleting a newly created head did not restore the default career.");
                    continue;
                }
                workspace.ExportHeadRon(ron); workspace.SetEditingBaseline(ron);
                workspace.SaveEditedRon(ron);
                TestAssert.True(original.SequenceEqual(File.ReadAllBytes(source)), "An untouched career changed bytes.");

                var head = workspace.Head;
                var scalar = head.MaterialData.Scalars.First();
                var modified = head with { MaterialData = head.MaterialData with
                {
                    Scalars = head.MaterialData.Scalars.Select(v => v.Name == scalar.Name ? v with { Value = v.Value + 0.125f } : v).ToArray()
                } };
                TseHeadMorphRon.Write(ron, modified);
                workspace.SaveEditedRon(ron);
                var reopened = PcsavHeadMorphWorkspace.Open(source);
                TestAssert.True(workspace.UnrelatedPrefix.SequenceEqual(reopened.UnrelatedPrefix) &&
                    workspace.UnrelatedSuffix.SequenceEqual(reopened.UnrelatedSuffix), "An unrelated career byte changed.");
                TestAssert.Near(scalar.Value + 0.125f,
                    reopened.Head.MaterialData.Scalars.Single(v => v.Name == scalar.Name).Value, 0.00001f);
                TestAssert.True(head.MaterialData.Textures.SequenceEqual(reopened.Head.MaterialData.Textures),
                    "A scalar edit changed native texture entries absent from the editing projection.");
                workspace.DeleteHeadMorph();
                var deleted = PcsavHeadMorphWorkspace.Open(source);
                TestAssert.True(!deleted.HasHeadMorph, "Delete left a native headmorph in the career.");
                TestAssert.True(workspace.UnrelatedPrefix.SequenceEqual(deleted.UnrelatedPrefix) &&
                    workspace.UnrelatedSuffix.SequenceEqual(deleted.UnrelatedSuffix), "Delete changed unrelated career data.");
                TseHeadMorphRon.Write(ron, head); workspace.SaveEditedRon(ron);
                TestAssert.True(original.SequenceEqual(File.ReadAllBytes(source)), "Returning to the original head changed the career.");
                TestAssert.True(original.SequenceEqual(File.ReadAllBytes(fixture)), "A fixture was mutated.");
            }
            finally { Directory.Delete(directory, recursive: true); }
        }
        TestAssert.Equal(6, customObserved.Count);
        TestAssert.Equal(3, defaultsObserved.Count);
        TestAssert.Equal(0xFC891918u, PcsavHeadMorphWorkspace.Crc32("123456789"u8));
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
                var ron = Path.Combine(directory, "head.ron"); ExportEditableHead(workspace, ron); workspace.SetEditingBaseline(ron);
                var corrupted = File.ReadAllBytes(source); corrupted[20] ^= 0x80; File.WriteAllBytes(source, corrupted);
                try { _ = PcsavHeadMorphWorkspace.Open(source); throw new Exception("A corrupt save was accepted."); }
                catch (PcsavException e) { TestAssert.Equal(PcsavError.InvalidFile, e.Reason); }
                try { workspace.SaveEditedRon(ron); throw new Exception("An externally changed career was overwritten."); }
                catch (PcsavException e) { TestAssert.Equal(PcsavError.SourceChanged, e.Reason); }
                TestAssert.True(corrupted.SequenceEqual(File.ReadAllBytes(source)), "A failed save changed its source.");
                TestAssert.Equal(0, Directory.GetFiles(directory, ".*.tmp").Length);
            }
            finally { Directory.Delete(directory, recursive: true); }
        }
    }

    private static void ExportEditableHead(PcsavHeadMorphWorkspace workspace, string ron)
    {
        if (workspace.HasHeadMorph) workspace.ExportHeadRon(ron);
        else new MorphFacePackageContextService().ExportRon(
            StandalonePlayerMorphImportService.ResolveInstalledSeed(workspace.Game),
            StandalonePlayerMorphImportService.ResolvePlayerTemplate(workspace.Game, workspace.Sex), ron);
    }

    private static void SaveAsCopies()
    {
        foreach (var fixture in Fixtures())
        {
            var directory = Path.Combine(Path.GetTempPath(), $"MFE-PcsavAs-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                var source = Path.Combine(directory, "original.pcsav"); File.Copy(fixture, source);
                var original = File.ReadAllBytes(source);
                var workspace = PcsavHeadMorphWorkspace.Open(source);
                var ron = Path.Combine(directory, "head.ron");
                ExportEditableHead(workspace, ron); workspace.SetEditingBaseline(ron);
                var copy = Path.Combine(directory, "copy.pcsav");
                workspace.SaveEditedRon(ron, copy);
                TestAssert.Equal(copy, workspace.SourcePath);
                var saved = PcsavHeadMorphWorkspace.Open(copy);
                TestAssert.True(saved.HasHeadMorph && original.SequenceEqual(File.ReadAllBytes(source)), "Save As modified its original.");
                workspace.DeleteHeadMorph();
                TestAssert.True(!PcsavHeadMorphWorkspace.Open(copy).HasHeadMorph && original.SequenceEqual(File.ReadAllBytes(source)),
                    "Saving after Save As targeted the original.");
            }
            finally { Directory.Delete(directory, true); }
        }
    }

    private static void HairTextureClearing()
    {
        var original = new TseHeadMorph("Test.OldHair", [], new([], [], []), new([], [], []));
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
            var head = new TseHeadMorph("None", [], new([new("nose_BridgeIn", 0.25f)], [], []), new([], [], []));
            var metadata = new RonExportProvenance(RonExportProducer.MFE, "0.9.6", MorphFaceGame.LE3, "HMM", true);
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
            File.WriteAllText(path, text.Replace("// MFE RON Content: Morphs", ""));
            try { TseHeadMorphRon.Validate(path); throw new Exception("An empty full head was accepted."); }
            catch (InvalidDataException) { }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
