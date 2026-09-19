using System.Numerics;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.Classes;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.LegendaryExplorer.TextureRegistry;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.Tests;

public static class CustomMeshPccMaterializerTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("C4 PCC export rejects Unreal SelectionColor parameters", SelectionColorIsNotSerializable),
        new("D1 shared PCC workflow preserves cycles and detects a lost saved edge", SharedWorkflowPreservesCyclesAndDetectsLostEdge),
        new("D1 every assignable material catalogue option exports through the corpus workflow", EveryAssignableMaterialExports),
        new("D1 every assignable material shares a package in both slot orders", EveryAssignableMaterialSharesPackage),
        new("D1 LE2 combined human skin lashes and eyes share exact dependency identities", Le2CombinedHumanMaterialsShareDependencies),
        new("D1 LE1 authored HMF lash retains and references the stock import identity", Le1AuthoredHmfLashPreservesImport),
        new("D1 LE2 human PCC preserves compiled graph and donor storage", Le2HumanPccUsesBiogTfcTextures),
        new("D1 LE2 human lash references one exact BIOG texture identity", Le2HumanLashUsesExactTextureIdentity),
        new("D1 custom PCC MIC references a package-qualified non-corpus texture", CustomMicUsesPackageQualifiedNonCorpusTexture),
        new("D1 LE2 Salarian PCC preserves effects graph and corpus roles", Le2SalarianPccFollowsOracleRoles),
        new("D1 human eye cube preserves all six face references", HumanEyeCubePreservesFaces),
        new("D1 LE3 Batarian PCC preserves physical-material and effects closure", Le3BatarianPccPreservesPhysicalMaterial),
        new("D1 racial PCC export reopens with complete graphs across games", RacialPccExportSucceedsAcrossGames)
    ];

    private static void SelectionColorIsNotSerializable()
    {
        TestAssert.True(!CustomMeshPccMaterializer.IsSerializableMaterialParameter("SelectionColor"),
            "SelectionColor must never survive into an authored MIC parameter array.");
        TestAssert.True(!CustomMeshPccMaterializer.IsSerializableMaterialParameter(" selectioncolor "),
            "SelectionColor filtering must tolerate case and whitespace differences.");
        TestAssert.True(CustomMeshPccMaterializer.IsSerializableMaterialParameter("SkinTone"),
            "Authored material parameters must remain serializable.");
    }

    private static void SharedWorkflowPreservesCyclesAndDetectsLostEdge()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var output = Path.Combine(Path.GetTempPath(), $"MFE-D1-Graph-{Guid.NewGuid():N}.pcc");
        try
        {
            PccDependencyGraphSnapshot snapshot;
            using (var source = MEPackageHandler.CreateMemoryEmptyPackage("D1GraphSource.pcc", MEGame.LE3))
            using (var destination = MEPackageHandler.CreateMemoryEmptyPackage(output, MEGame.LE3))
            {
                var sourceRoot = source.CreatePackageExport("D1Graph");
                var first = source.CreateExport("First", "Object", sourceRoot, indexed: false);
                var second = source.CreateExport("Second", "Object", sourceRoot, indexed: false);
                var shared = source.CreateExport("Shared", "Object", sourceRoot, indexed: false);
                first.WriteProperties([
                    new ObjectProperty(second, "Next"),
                    new ObjectProperty(shared, "Shared")
                ]);
                second.WriteProperties([
                    new ObjectProperty(first, "Next"),
                    new ObjectProperty(shared, "Shared")
                ]);

                var authoredRoot = destination.CreatePackageExport("MorphFaceEditor");
                var imported = PccPackageWorkflow.ImportDependencyGraph(
                    destination,
                    first,
                    authoredRoot,
                    textureCatalog: null,
                    preferBiogTextures: false);
                var reachable = ReachableExports(imported);
                TestAssert.True(reachable.Contains(imported), "The imported cycle lost its authored root.");
                TestAssert.Equal(3, reachable.Count);
                TestAssert.Equal(1, reachable.Count(value => value.ObjectNameString == "Shared"));
                snapshot = PccPackageWorkflow.CaptureDependencyGraph(imported);
                destination.Save(output);
            }

            using var reopened = MEPackageHandler.OpenMEPackage(output, forceLoadFromDisk: true);
            PccPackageWorkflow.VerifyDependencyGraph(reopened, snapshot);
            var secondSaved = reopened.Exports.Single(value => value.ObjectNameString == "Second");
            var properties = secondSaved.GetProperties();
            properties.RemoveNamedProperty("Next");
            secondSaved.WriteProperties(properties);
            var rejected = false;
            try
            {
                PccPackageWorkflow.VerifyDependencyGraph(reopened, snapshot);
            }
            catch (InvalidDataException exception)
            {
                rejected = exception.Message.Contains("reference slots", StringComparison.OrdinalIgnoreCase) ||
                           exception.Message.Contains("changed", StringComparison.OrdinalIgnoreCase);
            }
            TestAssert.True(rejected, "The reopened-graph verifier accepted a removed cycle edge.");
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    private static void Le1AuthoredHmfLashPreservesImport()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var store = new TextureRegistryStore(TextureRegistryPaths.CreateDefault());
        if (store.GetStatus(MorphFaceGame.LE1).State != TextureRegistryState.Ready)
        {
            throw new InvalidOperationException("The LE1 texture database is required for this D1 integration test.");
        }
        const string lashPath = "BIOG_Humanoid_MASTER_MTR_R.Human.HMF_HED_PROLash_Opac_M01";
        var textureCatalog = store.Read(MorphFaceGame.LE1).Candidates;
        var candidate = textureCatalog.SingleOrDefault(value =>
            value.InstancedPath.Equals(lashPath, StringComparison.OrdinalIgnoreCase));
        if (candidate is null)
        {
            throw new InvalidOperationException($"The LE1 texture database lacks '{lashPath}'.");
        }
        var option = new CustomMaterialTemplateCatalogService(new MorphFacePackageReader(), store)
            .Load(MorphFaceGame.LE1)
            .Options
            .FirstOrDefault(value => value.Label == "Human Female/Asari Lashes");
        if (option is null)
        {
            throw new InvalidOperationException("The LE1 HMF lash material template is unavailable.");
        }

        var occurrence = candidate.EffectiveOccurrence;
        var textureIdentity = new AssetIdentity(
            occurrence.PackagePath,
            lashPath,
            occurrence.ExportUIndex,
            "Texture2D");
        var materialData = new MorphFaceMaterialData(
            [],
            [],
            [new TextureMaterialOverride("HED_Lash_Diff", textureIdentity)]);
        var source = CreateTriangleMesh("c4-le1-hmf-lash.gltf");
        var workspace = new CustomMaterialWorkspace(source);
        workspace.Assign(0, option);
        var output = Path.Combine(Path.GetTempPath(), $"MFE-C4-LE1-Lash-{Guid.NewGuid():N}.pcc");
        try
        {
            _ = new CustomMeshPccMaterializer().Save(new CustomMeshPccSaveRequest(
                source,
                workspace,
                materialData,
                MorphFaceGame.LE1,
                output,
                "C4Le1LashFixture",
                new ImportedSkeletalMeshPackageWriter(),
                textureCatalog));

            using var package = MEPackageHandler.OpenMEPackage(output, forceLoadFromDisk: true);
            TestAssert.True(
                PackageIntegrity.FindExactEntry(package, lashPath, "Texture2D") is ImportEntry,
                "The LE1 stock HMF lash identity must remain an import, matching GlobalMorphs.");
            TestAssert.True(
                package.FindExport(lashPath, "Texture2D") is null,
                "The LE1 stock HMF lash import was incorrectly duplicated as an export.");
            var authoredLash = ResolveTextureParameter(FindAuthoredMic(package), "HED_Lash_Diff");
            TestAssert.True(
                authoredLash is ImportEntry &&
                authoredLash.InstancedFullPath.Equals(lashPath, StringComparison.OrdinalIgnoreCase),
                "The authored lash MIC does not reference the exact stock import identity.");
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    private static void EveryAssignableMaterialExports()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var store = new TextureRegistryStore(TextureRegistryPaths.CreateDefault());
        using var reader = new MorphFacePackageReader();
        foreach (var game in new[] { MorphFaceGame.LE3, MorphFaceGame.LE2, MorphFaceGame.LE1 })
        {
            if (store.GetStatus(game).State != TextureRegistryState.Ready)
            {
                throw new InvalidOperationException($"The {game} texture database is required for the D1 corpus matrix.");
            }
            var textureCatalog = store.Read(game).Candidates;
            var options = new CustomMaterialTemplateCatalogService(reader, store)
                .Load(game)
                .Options
                .OrderBy(value => value.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            TestAssert.True(options.Length > 0, $"The {game} assignable material catalogue is empty.");
            foreach (var option in options)
            {
                var source = CreateTriangleMesh($"d1-corpus-{game}-{SanitizeFileName(option.Id)}.gltf");
                var workspace = new CustomMaterialWorkspace(source);
                workspace.Assign(0, option);
                var liveData = new MorphFaceEditor.Core.Editing.MaterialEditingSession(
                    MorphFaceMaterialOverrides.Empty,
                    workspace.ActiveMaterials).CaptureInterchangeData();
                var data = MeshMaterialInterchange.Flatten(MeshMaterialFileService.Capture(
                    workspace,
                    game,
                    "D1Corpus",
                    liveData).Parameters);
                var output = Path.Combine(Path.GetTempPath(),
                    $"MFE-D1-Corpus-{game}-{Guid.NewGuid():N}.pcc");
                try
                {
                    try
                    {
                        _ = new CustomMeshPccMaterializer().Save(new CustomMeshPccSaveRequest(
                            source,
                            workspace,
                            data,
                            game,
                            output,
                            "D1Corpus",
                            new ImportedSkeletalMeshPackageWriter(),
                            textureCatalog));
                    }
                    catch (Exception exception)
                    {
                        throw new InvalidDataException(
                            $"{game} catalogue option '{option.Id}' ({option.Label}) failed corpus export: {exception.Message}",
                            exception);
                    }
                    using var package = MEPackageHandler.OpenMEPackage(output, forceLoadFromDisk: true);
                    PackageIntegrity.Verify(package);
                    VerifyKnownCorpusEntries(package, game);
                }
                finally
                {
                    if (File.Exists(output)) File.Delete(output);
                }
            }
        }
    }

    private static void EveryAssignableMaterialSharesPackage()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var store = new TextureRegistryStore(TextureRegistryPaths.CreateDefault());
        using var reader = new MorphFacePackageReader();
        foreach (var game in new[] { MorphFaceGame.LE3, MorphFaceGame.LE2, MorphFaceGame.LE1 })
        {
            if (store.GetStatus(game).State != TextureRegistryState.Ready)
            {
                throw new InvalidOperationException($"The {game} texture database is required for the D1 shared-package matrix.");
            }
            var textureCatalog = store.Read(game).Candidates;
            var options = new CustomMaterialTemplateCatalogService(reader, store)
                .Load(game)
                .Options
                .OrderBy(value => value.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var lead in options)
            {
                var selected = SelectCompatibleMaterialSet(lead, options);
                TestAssert.True(selected.Count > 1,
                    $"{game} catalogue option '{lead.Id}' had no compatible material for shared-package coverage.");
                ExerciseMaterialSet(game, selected, textureCatalog, $"{lead.Id}:forward");
                ExerciseMaterialSet(game, selected.Reverse().ToArray(), textureCatalog, $"{lead.Id}:reverse");
            }
        }
    }

    private static IReadOnlyList<CustomMaterialAssignmentOption> SelectCompatibleMaterialSet(
        CustomMaterialAssignmentOption lead,
        IReadOnlyList<CustomMaterialAssignmentOption> options)
    {
        var probe = new CustomMaterialWorkspace(CreateSlotMesh("d1-compatibility-probe.gltf", 4));
        var selected = new List<CustomMaterialAssignmentOption> { lead };
        probe.Assign(0, lead);
        var candidates = options
            .Where(value => !value.Id.Equals(lead.Id, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(value => value.EffectiveParameterScopeKey.Equals(
                lead.EffectiveParameterScopeKey, StringComparison.OrdinalIgnoreCase))
            .ThenBy(value => value.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            try
            {
                probe.Assign(selected.Count, candidate);
                selected.Add(candidate);
                if (selected.Count == 4)
                {
                    break;
                }
            }
            catch (ArgumentException)
            {
                // The workspace's compatibility contract is itself the filter.
            }
        }
        return selected;
    }

    private static void ExerciseMaterialSet(
        MorphFaceGame game,
        IReadOnlyList<CustomMaterialAssignmentOption> options,
        IReadOnlyList<TextureCatalogCandidate> textureCatalog,
        string caseName)
    {
        var source = CreateSlotMesh($"d1-shared-{game}-{SanitizeFileName(caseName)}.gltf", options.Count);
        var workspace = new CustomMaterialWorkspace(source);
        for (var index = 0; index < options.Count; index++)
        {
            workspace.Assign(index, options[index]);
        }
        var liveData = new MorphFaceEditor.Core.Editing.MaterialEditingSession(
            MorphFaceMaterialOverrides.Empty,
            workspace.ActiveMaterials).CaptureInterchangeData();
        var data = MeshMaterialInterchange.Flatten(MeshMaterialFileService.Capture(
            workspace,
            game,
            "D1Shared",
            liveData).Parameters);
        var output = Path.Combine(Path.GetTempPath(), $"MFE-D1-Shared-{game}-{Guid.NewGuid():N}.pcc");
        try
        {
            try
            {
                _ = new CustomMeshPccMaterializer().Save(new CustomMeshPccSaveRequest(
                    source,
                    workspace,
                    data,
                    game,
                    output,
                    "D1Shared",
                    new ImportedSkeletalMeshPackageWriter(),
                    textureCatalog));
            }
            catch (Exception exception)
            {
                throw new InvalidDataException(
                    $"{game} shared material case '{caseName}' failed with " +
                    $"[{string.Join(", ", options.Select(value => value.Label))}]: {exception.Message}",
                    exception);
            }
            using var package = MEPackageHandler.OpenMEPackage(output, forceLoadFromDisk: true);
            PackageIntegrity.Verify(package);
            VerifyKnownCorpusEntries(package, game);
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    private static void Le2CombinedHumanMaterialsShareDependencies()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var store = new TextureRegistryStore(TextureRegistryPaths.CreateDefault());
        if (store.GetStatus(MorphFaceGame.LE2).State != TextureRegistryState.Ready)
        {
            throw new InvalidOperationException("The LE2 texture database is required for this D1 integration test.");
        }
        var textureCatalog = store.Read(MorphFaceGame.LE2).Candidates;
        var options = new CustomMaterialTemplateCatalogService(new MorphFacePackageReader(), store)
            .Load(MorphFaceGame.LE2)
            .Options
            .Where(value => value.AppearanceCompatibilityKey.Equals("human-female", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var skin = options.First(value => value.Family == HeadMaterialFamily.Skin);
        var lashes = options.First(value => value.Family == HeadMaterialFamily.Lashes);
        var eyes = options.First(value => value.Family == HeadMaterialFamily.Eyes);
        var source = CreateThreeSlotMesh("d1-le2-combined-human.gltf");
        var workspace = new CustomMaterialWorkspace(source);
        // Put lashes first: real meshes do not guarantee a face-first slot
        // order, and the corpus role must not depend on which MIC is imported first.
        workspace.Assign(0, lashes);
        workspace.Assign(1, eyes);
        workspace.Assign(2, skin);
        var liveMaterialData = new MorphFaceEditor.Core.Editing.MaterialEditingSession(
            MorphFaceMaterialOverrides.Empty,
            workspace.ActiveMaterials).CaptureInterchangeData();
        var materialData = MeshMaterialInterchange.Flatten(
            MeshMaterialFileService.Capture(
                workspace,
                MorphFaceGame.LE2,
                "D1CombinedHuman",
                liveMaterialData).Parameters);
        const string stockLash = "BIOG_Humanoid_MASTER_MTR_R.Human.HMF_HED_PROLash_Opac_M01";
        var stockLashCandidate = textureCatalog.Single(value =>
            value.InstancedPath.Equals(stockLash, StringComparison.OrdinalIgnoreCase));
        var stockLashOccurrence = stockLashCandidate.EffectiveOccurrence;
        materialData = materialData with
        {
            Textures = materialData.Textures.Select(value =>
                MaterialParameterControlKey.ParameterName(value.Name)
                    .Equals("HED_Lash_Diff", StringComparison.OrdinalIgnoreCase)
                    ? value with
                    {
                        TextureReference = new AssetIdentity(
                            stockLashOccurrence.PackagePath,
                            stockLash,
                            stockLashOccurrence.ExportUIndex,
                            "Texture2D")
                    }
                    : value).ToArray()
        };
        TestAssert.True(materialData.Textures.Any(value =>
                value.TextureReference?.InstancedPath.Equals(stockLash, StringComparison.OrdinalIgnoreCase) == true),
            "The UI-facing combined fixture did not carry the exact stock HMF lash reference. Values: " +
            string.Join(", ", materialData.Textures.Select(value =>
                $"{value.Name}={value.TextureReference?.InstancedPath ?? "None"}")));
        var output = Path.Combine(Path.GetTempPath(), $"MFE-D1-CombinedHuman-{Guid.NewGuid():N}.pcc");
        try
        {
            _ = new CustomMeshPccMaterializer().Save(new CustomMeshPccSaveRequest(
                source,
                workspace,
                materialData,
                MorphFaceGame.LE2,
                output,
                "D1CombinedHuman",
                new ImportedSkeletalMeshPackageWriter(),
                textureCatalog));
            using var package = MEPackageHandler.OpenMEPackage(output, forceLoadFromDisk: true);
            PackageIntegrity.Verify(package);
            var savedStockLash = PackageIntegrity.FindExactEntry(package, stockLash, "Texture2D");
            TestAssert.True(
                savedStockLash is ExportEntry,
                "The combined material export did not retain the LE2 corpus-authored lash export role. " +
                $"Actual: {savedStockLash?.GetType().Name ?? "missing"} {savedStockLash?.InstancedFullPath ?? "None"}; " +
                "nearby: " + string.Join(", ", package.Exports.Concat<IEntry>(package.Imports)
                    .Where(value => value.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase) &&
                                    value.ObjectNameString.Contains("Lash", StringComparison.OrdinalIgnoreCase))
                    .Select(value => $"{value.GetType().Name}:{value.InstancedFullPath}")));
            TestAssert.Equal(3, package.Exports.Count(value =>
                value.Parent?.InstancedFullPath.Equals("MorphFaceEditor", StringComparison.OrdinalIgnoreCase) == true &&
                (value.ClassName.Equals("MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase) ||
                 value.ClassName.Equals("BioMaterialInstanceConstant", StringComparison.OrdinalIgnoreCase))));
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    private static void Le2HumanPccUsesBiogTfcTextures()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var store = new TextureRegistryStore(TextureRegistryPaths.CreateDefault());
        if (store.GetStatus(MorphFaceGame.LE2).State != TextureRegistryState.Ready)
        {
            throw new InvalidOperationException("The LE2 texture database is required for this D1 integration test.");
        }
        var textureCatalog = store.Read(MorphFaceGame.LE2).Candidates;

        var templates = new CustomMaterialTemplateCatalogService(
            new MorphFacePackageReader(),
            store).Load(MorphFaceGame.LE2);
        var option = templates.Options.FirstOrDefault(value =>
            value.AppearanceCompatibilityKey.Equals("human-female", StringComparison.OrdinalIgnoreCase) &&
            value.Family == HeadMaterialFamily.Skin);
        if (option is null)
        {
            throw new InvalidOperationException("The LE2 human skin material template is unavailable.");
        }

        var source = new ImportedMeshAsset(
            "c4-human-fixture.gltf",
            [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
            Enumerable.Repeat(Vector3.UnitZ, 3).ToArray(),
            Enumerable.Repeat(new Vector4(Vector3.UnitX, 1), 3).ToArray(),
            [Vector2.Zero, Vector2.UnitX, Vector2.UnitY],
            [0, 1, 2],
            [new ImportedMeshSection(0, "Face", 0, 3)],
            [], null, null, [0, 1, 2]);
        var workspace = new CustomMaterialWorkspace(source);
        workspace.Assign(0, option);
        var output = Path.Combine(Path.GetTempPath(), $"MFE-C4-LE2-Human-{Guid.NewGuid():N}.pcc");

        try
        {
            _ = new CustomMeshPccMaterializer().Save(new CustomMeshPccSaveRequest(
                source,
                workspace,
                new MorphFaceMaterialData([], [], []),
                MorphFaceGame.LE2,
                output,
                "C4HumanFixture",
                new ImportedSkeletalMeshPackageWriter(),
                textureCatalog));

            using var package = MEPackageHandler.OpenMEPackage(output, forceLoadFromDisk: true);
            var textures = package.Exports
                .Where(value => value.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            TestAssert.True(textures.Length > 0, "The human material graph exported no Texture2D dependencies.");
            TestAssert.True(textures.Any(value => new Texture2D(value).Mips.Any(mip =>
                    mip.storageType != StorageTypes.empty && !mip.IsPackageStored)),
                "Every texture mip was converted to package storage.");

            TestAssert.True(textures.All(value =>
                    value.InstancedFullPath.StartsWith("BIOG", StringComparison.OrdinalIgnoreCase)),
                "A human Texture2D was written at a character-creator/non-seekfree path instead of its BIOG identity.");
            TestAssert.True(package.Exports.Any(value =>
                    value.ClassName.StartsWith("MaterialExpression", StringComparison.OrdinalIgnoreCase)),
                "The compiled human Material lost its required expression graph.");

            const string stockMask = "Skin_HumanHED_SpecMulitplier_Mask";
            var mask = textures.SingleOrDefault(value =>
                value.ObjectNameString.Equals(stockMask, StringComparison.OrdinalIgnoreCase));
            TestAssert.True(mask is not null, "The LE2 compiled skin mask was not materialised.");
            TestAssert.True(new Texture2D(mask!).Mips.Any(mip =>
                    mip.storageType != StorageTypes.empty && mip.IsPackageStored),
                "The sole installed LE2PATCH skin-mask donor did not retain its package storage.");
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    private static void Le2HumanLashUsesExactTextureIdentity()
    {
        WithInstalledMaterial(
            MorphFaceGame.LE2,
            option => option.Label == "Human Female/Asari Lashes",
            package =>
            {
                var textures = package.Exports
                    .Where(value => value.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                TestAssert.True(textures.Any(value =>
                        value.ObjectNameString.Contains("Lash", StringComparison.OrdinalIgnoreCase)),
                    "The authored HMF lash texture was not materialised.");
                var referenced = ResolveTextureParameter(FindAuthoredMic(package), "HED_Lash_Diff");
                TestAssert.True(referenced is ExportEntry,
                    "The authored lash MIC does not reference a materialised Texture2D.");
                var referencedExport = (ExportEntry)referenced!;
                TestAssert.True(
                    referencedExport.InstancedFullPath.StartsWith("BIOG", StringComparison.OrdinalIgnoreCase),
                    "The authored lash MIC was not relinked to its exact BIOG texture identity.");
                TestAssert.Equal(1, package.Exports.Count(value =>
                    value.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase) &&
                    value.InstancedFullPath.Equals(referencedExport.InstancedFullPath, StringComparison.OrdinalIgnoreCase)));
            });
    }

    private static void CustomMicUsesPackageQualifiedNonCorpusTexture()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var store = new TextureRegistryStore(TextureRegistryPaths.CreateDefault());
        if (store.GetStatus(MorphFaceGame.LE2).State != TextureRegistryState.Ready)
        {
            throw new InvalidOperationException("The LE2 texture database is required for this D1 integration test.");
        }
        var textureCatalog = store.Read(MorphFaceGame.LE2).Candidates;
        var selected = textureCatalog
            .SelectMany(candidate => candidate.Occurrences
                .Where(occurrence => PccTextureDependencyResolver.IsSeekfreePackage(occurrence.PackagePath))
                .Select(occurrence => (Candidate: candidate, Occurrence: occurrence)))
            .Where(value => !value.Candidate.InstancedPath.StartsWith("BIO", StringComparison.OrdinalIgnoreCase))
            .Where(value => File.Exists(value.Occurrence.PackagePath))
            .Select(value => (
                value.Candidate,
                value.Occurrence,
                TargetPath: PccAssetPathPolicy.FromDonorOccurrence(
                    value.Candidate.InstancedPath,
                    value.Occurrence.PackagePath)))
            .FirstOrDefault(value => MaterialDependencyOracle.Instance.GetKind(
                MEGame.LE2,
                "Texture2D",
                value.TargetPath) == MaterialOracleEntryKind.Unknown);
        if (selected.Candidate is null)
        {
            throw new InvalidOperationException(
                "The LE2 registry contains no package-relative BIOG texture outside the Global Morphs oracle.");
        }

        var option = new CustomMaterialTemplateCatalogService(new MorphFacePackageReader(), store)
            .Load(MorphFaceGame.LE2)
            .Options
            .First(value =>
                value.EffectiveParameterScopeKey.Equals("human", StringComparison.OrdinalIgnoreCase) &&
                value.Template.SupportedTextures.Count > 0);
        var parameter = option.Template.SupportedTextures.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).First();
        var source = CreateTriangleMesh("d1-non-corpus-texture.gltf");
        var workspace = new CustomMaterialWorkspace(source);
        workspace.Assign(0, option);
        var materialData = new MorphFaceMaterialData(
            [],
            [],
            [new TextureMaterialOverride(
                parameter,
                new AssetIdentity(
                    selected.Occurrence.PackagePath,
                    selected.TargetPath,
                    selected.Occurrence.ExportUIndex,
                    "Texture2D"))]);
        var output = Path.Combine(Path.GetTempPath(), $"MFE-D1-NonCorpusMic-{Guid.NewGuid():N}.pcc");
        try
        {
            _ = new CustomMeshPccMaterializer().Save(new CustomMeshPccSaveRequest(
                source,
                workspace,
                materialData,
                MorphFaceGame.LE2,
                output,
                "D1NonCorpusMic",
                new ImportedSkeletalMeshPackageWriter(),
                textureCatalog));
            using var package = MEPackageHandler.OpenMEPackage(output, forceLoadFromDisk: true);
            var authoredTexture = ResolveTextureParameter(FindAuthoredMic(package), parameter);
            TestAssert.True(authoredTexture is ExportEntry,
                $"The authored {parameter} override was not materialised as a Texture2D export.");
            TestAssert.Equal(selected.TargetPath, authoredTexture!.InstancedFullPath);
            TestAssert.True(package.FindExport(selected.Candidate.InstancedPath, "Texture2D") is null,
                "The authored MIC retained an unqualified duplicate of the non-corpus BIOG texture.");
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    private static void Le2SalarianPccFollowsOracleRoles()
    {
        WithInstalledMaterial(
            MorphFaceGame.LE2,
            option => option.Family == HeadMaterialFamily.SalarianSkin,
            package =>
            {
                TestAssert.True(package.Exports.Any(value =>
                        value.ClassName.Equals("RvrEffectsMaterialUser", StringComparison.OrdinalIgnoreCase)),
                    "The Salarian render-chain RvrEffectsMaterialUser was not exported.");
                var user = package.Exports.First(value =>
                    value.ClassName.Equals("RvrEffectsMaterialUser", StringComparison.OrdinalIgnoreCase));
                TestAssert.True(
                    user.GetProperty<ObjectProperty>("m_pMultiplexor")?.ResolveToEntry(package) is not null &&
                    user.GetProperty<ObjectProperty>("m_pParentMaterial")?.ResolveToEntry(package) is not null,
                    "The Salarian effects wrapper lost its multiplexor or parent-material edge.");

                foreach (var export in package.Exports.Where(value =>
                             IsOracleDependencyClass(value.ClassName) &&
                             !value.InstancedFullPath.StartsWith("MorphFaceEditor.", StringComparison.OrdinalIgnoreCase)))
                {
                    TestAssert.Equal(
                        MaterialOracleEntryKind.Export,
                        MaterialDependencyOracle.Instance.GetKind(package.Game, export.ClassName, export.InstancedFullPath));
                }
                foreach (var import in package.Imports.Where(value => IsOracleDependencyClass(value.ClassName)))
                {
                    TestAssert.Equal(
                        MaterialOracleEntryKind.Import,
                        MaterialDependencyOracle.Instance.GetKind(package.Game, import.ClassName, import.InstancedFullPath));
                }
            });
    }

    private static void HumanEyeCubePreservesFaces()
    {
        WithInstalledMaterial(
            MorphFaceGame.LE3,
            option => option.AppearanceCompatibilityKey.Equals("human-female", StringComparison.OrdinalIgnoreCase) &&
                      option.Family == HeadMaterialFamily.Eyes,
            package =>
            {
                var cube = ReachableExports(FindAuthoredMic(package)).FirstOrDefault(value =>
                    value.ClassName.Equals("TextureCube", StringComparison.OrdinalIgnoreCase));
                TestAssert.True(cube is not null, "The human eye material graph contains no reachable TextureCube.");
                foreach (var faceName in new[] { "FacePosX", "FaceNegX", "FacePosY", "FaceNegY", "FacePosZ", "FaceNegZ" })
                {
                    TestAssert.True(
                        cube!.GetProperty<ObjectProperty>(faceName)?.ResolveToEntry(package) is ExportEntry face &&
                        face.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase),
                        $"The eye reflection cube lost its {faceName} Texture2D edge.");
                }
            });
    }

    private static void RacialPccExportSucceedsAcrossGames()
    {
        var cases = new[]
        {
            (MorphFaceGame.LE1, HeadMaterialFamily.TurianSkin),
            (MorphFaceGame.LE2, HeadMaterialFamily.SalarianEyes),
            (MorphFaceGame.LE3, HeadMaterialFamily.KroganSkin)
        };
        foreach (var (game, family) in cases)
        {
            WithInstalledMaterial(
                game,
                option => option.Family == family,
                package =>
                {
                    TestAssert.True(package.Exports.Any(value =>
                            value.ClassName.Equals("RvrEffectsMaterialUser", StringComparison.OrdinalIgnoreCase) ||
                            value.ClassName.Equals("Material", StringComparison.OrdinalIgnoreCase)),
                        $"{game} {family} exported no render-chain material.");
                    TestAssert.True(package.Exports.Any(value =>
                            value.ClassName.StartsWith("MaterialExpression", StringComparison.OrdinalIgnoreCase)),
                        $"{game} {family} lost its compiled material expression graph.");
                    PackageIntegrity.Verify(package);
                });
        }
    }

    private static void Le3BatarianPccPreservesPhysicalMaterial()
    {
        WithInstalledMaterial(
            MorphFaceGame.LE3,
            option => option.Family == HeadMaterialFamily.BatarianSkin,
            package =>
            {
                var baseMaterial = package.Exports.Single(value =>
                    value.ClassName.Equals("Material", StringComparison.OrdinalIgnoreCase) &&
                    value.InstancedFullPath.EndsWith("BAT_HED_PRO_MASTER_MAT", StringComparison.OrdinalIgnoreCase));
                var physicalMaterial = baseMaterial.GetProperty<ObjectProperty>("PhysMaterial")?
                    .ResolveToEntry(package) as ExportEntry;
                TestAssert.True(
                    physicalMaterial is not null &&
                    physicalMaterial.ClassName.Equals("PhysicalMaterial", StringComparison.OrdinalIgnoreCase) &&
                    physicalMaterial.InstancedFullPath.Equals(
                        "BIOG_PHM_Characters_F.PHM_HMM_ARM_Red",
                        StringComparison.OrdinalIgnoreCase),
                    "The LE3 Batarian base material lost its exact PhysMaterial edge.");
                TestAssert.True(ReachableExports(physicalMaterial!).Any(value =>
                        value.ClassName.StartsWith("SFXPhysicalMaterial", StringComparison.OrdinalIgnoreCase)),
                    "The LE3 Batarian physical-material property chain is incomplete.");
                var user = package.Exports.FirstOrDefault(value =>
                    value.ClassName.Equals("RvrEffectsMaterialUser", StringComparison.OrdinalIgnoreCase) &&
                    ReachableExports(value).Contains(baseMaterial));
                TestAssert.True(user?.GetProperty<ObjectProperty>("m_pMultiplexor")?.ResolveToEntry(package) is not null,
                    "The LE3 Batarian effects wrapper lost its multiplexor graph.");
            });
    }

    private static void WithInstalledMaterial(
        MorphFaceGame game,
        Func<CustomMaterialAssignmentOption, bool> selector,
        Action<IMEPackage> assertion)
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var store = new TextureRegistryStore(TextureRegistryPaths.CreateDefault());
        if (store.GetStatus(game).State != TextureRegistryState.Ready)
        {
            throw new InvalidOperationException($"The {game} texture database is required for this D1 integration test.");
        }
        var textureCatalog = store.Read(game).Candidates;
        var option = new CustomMaterialTemplateCatalogService(
                new MorphFacePackageReader(),
                store)
            .Load(game)
            .Options
            .FirstOrDefault(selector);
        if (option is null)
        {
            throw new InvalidOperationException($"The requested {game} material template is unavailable.");
        }

        var source = CreateTriangleMesh("c4-material-oracle-fixture.gltf");
        var workspace = new CustomMaterialWorkspace(source);
        workspace.Assign(0, option);
        var output = Path.Combine(Path.GetTempPath(), $"MFE-C4-Material-{Guid.NewGuid():N}.pcc");
        try
        {
            _ = new CustomMeshPccMaterializer().Save(new CustomMeshPccSaveRequest(
                source,
                workspace,
                new MorphFaceMaterialData([], [], []),
                game,
                output,
                "C4MaterialFixture",
                new ImportedSkeletalMeshPackageWriter(),
                textureCatalog));
            using var package = MEPackageHandler.OpenMEPackage(output, forceLoadFromDisk: true);
            assertion(package);
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    private static ImportedMeshAsset CreateTriangleMesh(string sourcePath) => new(
        sourcePath,
        [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
        Enumerable.Repeat(Vector3.UnitZ, 3).ToArray(),
        Enumerable.Repeat(new Vector4(Vector3.UnitX, 1), 3).ToArray(),
        [Vector2.Zero, Vector2.UnitX, Vector2.UnitY],
        [0, 1, 2],
        [new ImportedMeshSection(0, "Material", 0, 3)],
        [], null, null, [0, 1, 2]);

    private static ImportedMeshAsset CreateThreeSlotMesh(string sourcePath) => new(
        sourcePath,
        Enumerable.Range(0, 9).Select(index => new Vector3(index % 3, index / 3, 0)).ToArray(),
        Enumerable.Repeat(Vector3.UnitZ, 9).ToArray(),
        Enumerable.Repeat(new Vector4(Vector3.UnitX, 1), 9).ToArray(),
        Enumerable.Repeat(Vector2.Zero, 9).ToArray(),
        Enumerable.Range(0, 9).ToArray(),
        [
            new ImportedMeshSection(0, "Skin", 0, 3),
            new ImportedMeshSection(1, "Lashes", 3, 3),
            new ImportedMeshSection(2, "Eyes", 6, 3)
        ],
        [], null, null, Enumerable.Range(0, 9).ToArray());

    private static ImportedMeshAsset CreateSlotMesh(string sourcePath, int slotCount)
    {
        var vertexCount = slotCount * 3;
        return new ImportedMeshAsset(
            sourcePath,
            Enumerable.Range(0, vertexCount).Select(index => new Vector3(index % 3, index / 3, 0)).ToArray(),
            Enumerable.Repeat(Vector3.UnitZ, vertexCount).ToArray(),
            Enumerable.Repeat(new Vector4(Vector3.UnitX, 1), vertexCount).ToArray(),
            Enumerable.Repeat(Vector2.Zero, vertexCount).ToArray(),
            Enumerable.Range(0, vertexCount).ToArray(),
            Enumerable.Range(0, slotCount)
                .Select(index => new ImportedMeshSection(index, $"Material {index}", index * 3, 3))
                .ToArray(),
            [],
            null,
            null,
            Enumerable.Range(0, vertexCount).ToArray());
    }

    private static void VerifyKnownCorpusEntries(IMEPackage package, MorphFaceGame game)
    {
        var meGame = game switch
        {
            MorphFaceGame.LE1 => MEGame.LE1,
            MorphFaceGame.LE2 => MEGame.LE2,
            MorphFaceGame.LE3 => MEGame.LE3,
            _ => throw new ArgumentOutOfRangeException(nameof(game), game, null)
        };
        foreach (var entry in package.Exports.Cast<IEntry>().Concat(package.Imports)
                     .Where(value => !value.ClassName.Equals("Package", StringComparison.OrdinalIgnoreCase)))
        {
            var kind = MaterialDependencyOracle.Instance.GetKind(
                meGame,
                entry.ClassName,
                entry.InstancedFullPath);
            if (kind == MaterialOracleEntryKind.Unknown)
            {
                continue;
            }
            var actual = entry is ExportEntry
                ? MaterialOracleEntryKind.Export
                : MaterialOracleEntryKind.Import;
            TestAssert.Equal(kind, actual);
        }
    }

    private static string SanitizeFileName(string value) => new(value
        .Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)
        .ToArray());

    private static bool IsOracleDependencyClass(string className) =>
        className.Equals("Material", StringComparison.OrdinalIgnoreCase) ||
        className.Equals("RvrEffectsMaterialUser", StringComparison.OrdinalIgnoreCase) ||
        className.Equals("Texture2D", StringComparison.OrdinalIgnoreCase) ||
        className.Equals("TextureCube", StringComparison.OrdinalIgnoreCase);

    private static ExportEntry FindAuthoredMic(IMEPackage package) =>
        package.Exports.Single(value =>
            value.Parent?.InstancedFullPath.Equals("MorphFaceEditor", StringComparison.OrdinalIgnoreCase) == true &&
            (value.ClassName.Equals("MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase) ||
             value.ClassName.Equals("BioMaterialInstanceConstant", StringComparison.OrdinalIgnoreCase)));

    private static IEntry? ResolveTextureParameter(ExportEntry material, string parameterName)
    {
        var value = material.GetProperty<ArrayProperty<StructProperty>>("TextureParameterValues")?
            .SingleOrDefault(item => item.GetProp<NameProperty>("ParameterName")?.Value.Instanced
                .Equals(parameterName, StringComparison.OrdinalIgnoreCase) == true);
        return value?.GetProp<ObjectProperty>("ParameterValue")?.ResolveToEntry(material.FileRef);
    }

    private static IReadOnlySet<ExportEntry> ReachableExports(ExportEntry root)
    {
        var result = new HashSet<ExportEntry>();
        var pending = new Queue<ExportEntry>();
        pending.Enqueue(root);
        while (pending.TryDequeue(out var current))
        {
            if (!result.Add(current))
            {
                continue;
            }
            foreach (var target in PackageIntegrity.References(current).Values
                         .Select(current.FileRef.GetEntry)
                         .OfType<ExportEntry>())
            {
                pending.Enqueue(target);
            }
        }
        return result;
    }

}
