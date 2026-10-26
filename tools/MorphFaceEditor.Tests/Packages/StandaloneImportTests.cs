using MorphFaceEditor.Core.Services;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;

using static MorphFaceEditor.Tests.PackageTestFixtures;

namespace MorphFaceEditor.Tests;

internal static class StandaloneImportTests
{
    internal static IReadOnlyList<TestCase> All { get; } =
    [
        new("full RON replacement omits unavailable textures without same-name substitution", RonReplacementOmitsUnavailableTextures),
        new("unresolved standalone RON attachments are rejected before PCC publication", StandaloneRonUnresolvedAttachmentFailsAtomically),
        new("standalone player RON sex detection uses per-game LOD0 topology", StandalonePlayerRonSexDetection),
        new("cancelled standalone NPC construction preserves the prior workspace and cleans PCC candidates", CancelledStandaloneNpcConstructionCleansCandidates),
        new("standalone NPC RON rejects cross-game LE3 routing before publication", StandaloneNpcRonRejectsLe3CrossGame),
    ];

    private static void RonReplacementOmitsUnavailableTextures()
    {
        WithPackageCopy("LE1 GlobalMorphs.pcc", path =>
        {
            var service = new MorphFacePackageContextService();
            var source = ReadFaces(path).First(face =>
                service.CaptureMaterialData(path, face.InstancedPath).Textures.Any(value => value.TextureReference is not null));
            var ronPath = Path.Combine(Path.GetTempPath(), $"MFE-{Guid.NewGuid():N}.ron");
            var sourceMaterial = service.CaptureMaterialData(path, source.InstancedPath);
            var originalPath = sourceMaterial.Textures.First(value => value.TextureReference is not null)
                .TextureReference!.InstancedPath;
            var missingPath = "BIOG_MFE_Missing_Donor." + originalPath.Split('.').Last();
            try
            {
                service.ExportRon(path, source.InstancedPath, ronPath);
                var original = File.ReadAllText(ronPath);
                File.WriteAllText(ronPath, original.Replace(
                    $"\"{originalPath}\"", $"\"{missingPath}\"", StringComparison.Ordinal));

                var result = service.ReplaceHead(path, source.InstancedPath, TseHeadMorphRon.Read(ronPath), player: false);
                TestAssert.True(result.UserWarnings.Any(warning => warning.LocalizationKey == "loc_RonImport_TextureSkipped" &&
                    warning.Arguments.Contains(missingPath)), "The omitted RON texture warning lost its exact missing path.");
                TestAssert.True(service.CaptureMaterialData(path, source.InstancedPath).Textures.All(value =>
                    value.TextureReference?.InstancedPath != originalPath), "The unresolved texture used a same-name substitute.");
            }
            finally
            {
                if (File.Exists(ronPath))
                {
                    File.Delete(ronPath);
                }
            }
        });
    }

    private static void StandaloneRonUnresolvedAttachmentFailsAtomically()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        WithPackageCopy("LE3 GlobalMorphs.pcc", path =>
        {
            var service = new MorphFacePackageContextService();
            string facePath;
            string hairPath;
            int hairUIndex;
            using (var package = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true))
            {
                var face = package.Exports.First(candidate =>
                    candidate.ClassName.Equals("BioMorphFace", StringComparison.OrdinalIgnoreCase) &&
                    candidate.GetProperty<ObjectProperty>("m_oHairMesh")?.ResolveToEntry(package) is not null);
                facePath = face.InstancedFullPath;
                var hair = face.GetProperty<ObjectProperty>("m_oHairMesh")!.ResolveToEntry(package)!;
                hairPath = hair.InstancedFullPath;
                hairUIndex = hair.UIndex;
            }

            var ronPath = Path.Combine(Path.GetTempPath(), $"MFE-RonMissingHair-{Guid.NewGuid():N}.ron");
            const string missingPath = "BIOG_MFE_Missing_Hair.Hair_Missing.HMF_HIR_Missing_MDL";
            try
            {
                // Reproduce a cloned-template import placeholder with an exact donor candidate.
                using (var package = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true))
                {
                    _ = MorphFaceAttachmentTransferEngine.EnsureImport(package, missingPath);
                    package.Save();
                }
                var sourceFingerprint = PackageFingerprint.Capture(path);
                service.ExportRon(path, facePath, ronPath);
                var original = File.ReadAllText(ronPath);
                File.WriteAllText(ronPath, original.Replace(
                    $"hair_mesh: \"{hairPath}\"",
                    $"hair_mesh: \"{missingPath}\"",
                    StringComparison.Ordinal));

                var threw = false;
                try
                {
                    _ = service.ImportStandalonePlayerHead(
                        path,
                        facePath,
                        "MFE_UnresolvedHair",
                        TseHeadMorphRon.Read(ronPath),
                        new StandalonePlayerAssetCatalog(
                            MorphFaceGame.LE3,
                            [],
                            [new AssetIdentity(path, missingPath, hairUIndex, "SkeletalMesh")]));
                }
                catch (InvalidDataException exception)
                {
                    threw = exception.Message.Contains(missingPath, StringComparison.Ordinal) &&
                        (exception.Message.Contains("import placeholder", StringComparison.OrdinalIgnoreCase) ||
                         exception.Message.Contains("cannot be exported safely", StringComparison.OrdinalIgnoreCase));
                }

                TestAssert.True(threw,
                    "An unresolved standalone RON hair mesh was accepted as a placeholder import.");
                TestAssert.Equal(sourceFingerprint, PackageFingerprint.Capture(path));
                TestAssert.True(ReadFaces(path).All(face =>
                    !face.ObjectName.Equals("MFE_UnresolvedHair", StringComparison.OrdinalIgnoreCase)),
                    "An unresolved standalone hair import left a partial BioMorphFace in the package.");
            }
            finally
            {
                if (File.Exists(ronPath))
                {
                    File.Delete(ronPath);
                }
            }
        });
    }

    private static void StandalonePlayerRonSexDetection()
    {
        foreach (var game in new[] { MorphFaceGame.LE1, MorphFaceGame.LE2 })
        {
            TestAssert.Equal(StandalonePlayerSex.Male,
                StandalonePlayerMorphImportService.IdentifySex(game, 2294));
            TestAssert.Equal(StandalonePlayerSex.Female,
                StandalonePlayerMorphImportService.IdentifySex(game, 2232));
        }
        TestAssert.Equal(StandalonePlayerSex.Male,
            StandalonePlayerMorphImportService.IdentifySex(MorphFaceGame.LE3, 2392));
        TestAssert.Equal(StandalonePlayerSex.Female,
            StandalonePlayerMorphImportService.IdentifySex(MorphFaceGame.LE3, 2390));

        var rejected = false;
        try
        {
            _ = StandalonePlayerMorphImportService.IdentifySex(MorphFaceGame.LE3, 2294);
        }
        catch (InvalidDataException exception)
        {
            rejected = exception.Message.Contains("expected 2392", StringComparison.Ordinal) &&
                       exception.Message.Contains("2390", StringComparison.Ordinal);
        }
        TestAssert.True(rejected, "LE3 accepted an LE1/LE2 player topology or returned an unclear error.");
    }

    private static void CancelledStandaloneNpcConstructionCleansCandidates()
    {
        WithPackageCopy("LE2 GlobalMorphs.pcc", donorPath =>
        {
            var context = new MorphFacePackageContextService();
            var donor = ReadFaces(donorPath).First(face =>
                face.ProfileKey.Equals("le2-human-male", StringComparison.OrdinalIgnoreCase));
            var ronPath = Path.Combine(Path.GetTempPath(), $"MFE-StandaloneNpc-Cancel-{Guid.NewGuid():N}.ron");
            using var priorWorkspace = new MorphFacePackageWorkspace(donorPath, canCommit: false);
            var priorBytes = File.ReadAllBytes(priorWorkspace.WorkingPath);
            var donorBytes = File.ReadAllBytes(donorPath);
            var workspaceDirectory = Path.GetDirectoryName(priorWorkspace.WorkingPath)!;
            var filesBefore = Directory.EnumerateFiles(workspaceDirectory)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            using var cancellation = new CancellationTokenSource();
            var interruptionReached = false;
            try
            {
                context.ExportRon(donorPath, donor.InstancedPath, ronPath);
                FileSaveTransaction.BeforeCommitForTesting = (temporaryPath, destination) =>
                {
                    TestAssert.True(temporaryPath.StartsWith(workspaceDirectory, StringComparison.OrdinalIgnoreCase),
                        "NPC cancellation did not interrupt an AppData candidate PCC.");
                    TestAssert.True(destination.StartsWith(workspaceDirectory, StringComparison.OrdinalIgnoreCase),
                        "NPC cancellation reached an installed PCC path.");
                    TestAssert.True(!string.Equals(destination, priorWorkspace.WorkingPath,
                        StringComparison.OrdinalIgnoreCase),
                        "NPC cancellation targeted the prior workspace instead of the off-screen candidate.");
                    interruptionReached = true;
                    cancellation.Cancel();
                    throw new OperationCanceledException(cancellation.Token);
                };

                var cancelled = false;
                try
                {
                    _ = new StandaloneNpcMorphImportService().ImportNpcHead(
                        new StandaloneNpcMorphImportRequest(
                            MorphFaceGame.LE2,
                            MorphFaceGame.LE2,
                            "HMM",
                            TseHeadMorphRon.Read(ronPath),
                            donorPath,
                            donor.InstancedPath,
                            "MFE_Cancelled_Npc",
                            IsVerifiedNativeNpcDonor: true,
                            CancellationToken: cancellation.Token));
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }

                TestAssert.True(cancelled && cancellation.IsCancellationRequested,
                    "Cancelling during NPC candidate construction did not stop the import.");
                TestAssert.True(interruptionReached,
                    "NPC cancellation did not reach the pre-install candidate boundary.");
                TestAssert.True(donorBytes.SequenceEqual(File.ReadAllBytes(donorPath)),
                    "NPC cancellation changed the installed donor PCC.");
                TestAssert.True(priorBytes.SequenceEqual(File.ReadAllBytes(priorWorkspace.WorkingPath)),
                    "NPC cancellation changed the prior workspace.");
                var filesAfter = Directory.EnumerateFiles(workspaceDirectory)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                TestAssert.True(filesAfter.SetEquals(filesBefore),
                    "NPC cancellation left a candidate workspace or temporary PCC in AppData.");
            }
            finally
            {
                FileSaveTransaction.BeforeCommitForTesting = null;
                if (File.Exists(ronPath)) File.Delete(ronPath);
            }
        });
    }

    private static void StandaloneNpcRonRejectsLe3CrossGame()
    {
        WithPackageCopy("LE2 GlobalMorphs.pcc", donorPath =>
        {
            var context = new MorphFacePackageContextService();
            var donor = ReadFaces(donorPath).First(face =>
                face.ProfileKey.Equals("le2-human-male", StringComparison.OrdinalIgnoreCase));
            var ronPath = Path.Combine(Path.GetTempPath(), $"MFE-StandaloneNpc-Le3-{Guid.NewGuid():N}.ron");
            try
            {
                context.ExportRon(donorPath, donor.InstancedPath, ronPath);
                var rejected = false;
                try
                {
                    _ = new StandaloneNpcMorphImportService().ImportNpcHead(
                        new StandaloneNpcMorphImportRequest(
                            MorphFaceGame.LE3,
                            MorphFaceGame.LE2,
                            "HMM",
                            TseHeadMorphRon.Read(ronPath),
                            donorPath,
                            donor.InstancedPath,
                            "MFE_Invalid_Npc"));
                }
                catch (InvalidDataException exception)
                {
                    rejected = exception.Message.Contains("LE3", StringComparison.OrdinalIgnoreCase) &&
                               exception.Message.Contains("cross-game", StringComparison.OrdinalIgnoreCase);
                }

                TestAssert.True(rejected,
                    "The NPC service accepted an LE3 cross-game route instead of leaving it to explicit conversion.");
            }
            finally
            {
                if (File.Exists(ronPath))
                {
                    File.Delete(ronPath);
                }
            }
        });
    }
}
