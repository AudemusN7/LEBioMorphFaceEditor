using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Unreal;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.LegendaryExplorer;

/// <summary>
/// The binary mesh writer is deliberately supplied by the caller.  This keeps
/// material/package authoring independent from the PSK/glTF conversion code and
/// gives all supported interchange formats one PCC save path.
/// </summary>
public interface ICustomMeshBinaryWriter
{
    ExportEntry Write(
        IMEPackage package,
        ExportEntry packageRoot,
        ImportedMeshAsset source,
        string meshName,
        IReadOnlyList<CustomMeshMaterialReference> materials);
}

/// <summary>One imported slot and the MIC which must occupy that slot.</summary>
public sealed record CustomMeshMaterialReference(int MaterialIndex, ExportEntry Material);

public sealed record CustomMeshPccSaveRequest(
    ImportedMeshAsset SourceMesh,
    CustomMaterialWorkspace Workspace,
    MorphFaceMaterialData MaterialData,
    MorphFaceGame Game,
    string DestinationPath,
    string MeshName,
    ICustomMeshBinaryWriter MeshWriter,
    IReadOnlyList<TextureCatalogCandidate> TextureCatalog);

public sealed record CustomMeshPccMaterialResult(
    int MaterialIndex,
    string MaterialPath,
    string SourceMaterialPath,
    string Scope,
    IReadOnlyList<string> Parameters);

public sealed record CustomMeshPccSaveResult(
    string PackagePath,
    string PackageRootPath,
    string MeshPath,
    IReadOnlyList<CustomMeshPccMaterialResult> Materials,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Creates the new PCC used by a detached MESH workspace.  Attachments are not
/// part of this request and therefore cannot accidentally be written to the
/// authoring package.
/// </summary>
public sealed class CustomMeshPccMaterializer
{
    private const string PackageRootName = "MorphFaceEditor";

    public CustomMeshPccSaveResult Save(CustomMeshPccSaveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.SourceMesh);
        ArgumentNullException.ThrowIfNull(request.Workspace);
        ArgumentNullException.ThrowIfNull(request.MaterialData);
        ArgumentNullException.ThrowIfNull(request.MeshWriter);
        ValidateRequest(request);
        LegendaryExplorerCoreRuntime.Initialize();

        var destination = Path.GetFullPath(request.DestinationPath);
        var directory = Path.GetDirectoryName(destination)
                        ?? throw new InvalidOperationException("The PCC destination has no parent directory.");
        Directory.CreateDirectory(directory);
        var originalDestination = File.Exists(destination)
            ? PackageFingerprint.Capture(destination)
            : null;
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.mfe-mesh.tmp.pcc");

        try
        {
            var warnings = new List<string>();
            IReadOnlyList<CustomMeshPccMaterialResult> materialResults;
            PccDependencyGraphSnapshot dependencyGraph;
            string meshPath;
            using (var package = MEPackageHandler.CreateMemoryEmptyPackage(
                       temporaryPath,
                       ToMeGame(request.Game)))
            {
                var packageRoot = package.CreateExport(PackageRootName, "Package", indexed: false);
                var materials = MaterializeMaterials(package, packageRoot, request, warnings,
                    out materialResults);
                var mesh = request.MeshWriter.Write(
                    package,
                    packageRoot,
                    request.SourceMesh,
                    request.MeshName,
                    materials);
                if (mesh is null || !mesh.ClassName.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The custom mesh writer did not return a SkeletalMesh export.");
                }
                if (!ReferenceEquals(mesh.Parent, packageRoot))
                {
                    throw new InvalidDataException(
                        $"The custom mesh writer placed '{mesh.InstancedFullPath}' outside '{packageRoot.InstancedFullPath}'.");
                }

                meshPath = mesh.InstancedFullPath;
                dependencyGraph = PccPackageWorkflow.CaptureDependencyGraph(mesh);
                package.Save(temporaryPath);
            }

            Verify(temporaryPath, request, materialResults, meshPath, dependencyGraph);
            if (originalDestination is null)
            {
                if (File.Exists(destination))
                {
                    throw new IOException("The PCC destination was created by another process. Nothing was replaced.");
                }
            }
            else if (!File.Exists(destination) || PackageFingerprint.Capture(destination) != originalDestination)
            {
                throw new IOException("The PCC destination changed while it was being written. Nothing was replaced.");
            }

            PccPackageWorkflow.AtomicReplace(temporaryPath, destination);
            return new CustomMeshPccSaveResult(
                destination,
                $"{PackageRootName}",
                meshPath,
                materialResults,
                warnings);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static IReadOnlyList<CustomMeshMaterialReference> MaterializeMaterials(
        IMEPackage destination,
        ExportEntry packageRoot,
        CustomMeshPccSaveRequest request,
        ICollection<string> warnings,
        out IReadOnlyList<CustomMeshPccMaterialResult> results)
    {
        var scoped = MeshMaterialInterchange.Split(request.MaterialData);
        var used = request.Workspace.UsedSlots
            .OrderBy(slot => slot.MaterialIndex)
            .ToArray();
        var assignments = request.Workspace.Assignments
            .ToDictionary(value => value.Slot.MaterialIndex);
        var output = new List<CustomMeshMaterialReference>(used.Length);
        var materialResults = new List<CustomMeshPccMaterialResult>(used.Length);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var materialByKey = new Dictionary<string, ExportEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var slot in used)
        {
            if (!assignments.TryGetValue(slot.MaterialIndex, out var assignment))
            {
                throw new InvalidDataException($"Material slot {slot.MaterialIndex} has no assigned material.");
            }

            var option = assignment.Option;
            var sourceIdentity = option.Template.Source;
            var materialKey = MaterialIdentityKey.Create(sourceIdentity) + "|" +
                              option.EffectiveParameterScopeKey;
            if (!materialByKey.TryGetValue(materialKey, out var material))
            {
                var preferBiogTextures = option.EffectiveParameterScopeKey.Equals(
                    "human",
                    StringComparison.OrdinalIgnoreCase);
                material = ImportMaterialSource(
                    destination,
                    packageRoot,
                    sourceIdentity,
                    request.TextureCatalog,
                    preferBiogTextures,
                    warnings);
                var materialName = MakeMaterialName(
                    request.MeshName,
                    MaterialToken(option),
                    usedNames);
                material.ObjectName = new NameReference(materialName);
                var sourceScope = GetScopeData(request.MaterialData, scoped, option.EffectiveParameterScopeKey);
                var parameterNames = WriteMaterialParameters(
                    destination,
                    material,
                    option.Template,
                    sourceScope,
                    request.TextureCatalog,
                    preferBiogTextures,
                    warnings);
                materialByKey.Add(materialKey, material);
                materialResults.Add(new CustomMeshPccMaterialResult(
                    slot.MaterialIndex,
                    material.InstancedFullPath,
                    sourceIdentity.InstancedPath,
                    option.EffectiveParameterScopeKey,
                    parameterNames));
            }
            output.Add(new CustomMeshMaterialReference(slot.MaterialIndex, material));
        }

        results = materialResults;
        return output;
    }

    private static ExportEntry ImportMaterialSource(
        IMEPackage destination,
        ExportEntry packageRoot,
        AssetIdentity identity,
        IReadOnlyList<TextureCatalogCandidate> textureCatalog,
        bool preferBiogTextures,
        ICollection<string> warnings)
    {
        if (!File.Exists(identity.PackagePath))
        {
            throw new FileNotFoundException(
                $"The selected material source package no longer exists: {identity.PackagePath}",
                identity.PackagePath);
        }
        using var source = MEPackageHandler.OpenMEPackage(identity.PackagePath, forceLoadFromDisk: true);
        if (source.Game != destination.Game)
        {
            throw new InvalidDataException(
                $"Material '{identity.InstancedPath}' is from {source.Game}, but the destination is {destination.Game}.");
        }
        var sourceExport = ResolveMaterialExport(source, identity);
        return PccPackageWorkflow.ImportDependencyGraph(
            destination,
            sourceExport,
            packageRoot,
            textureCatalog,
            preferBiogTextures,
            warnings);
    }

    private static ExportEntry ResolveMaterialExport(IMEPackage source, AssetIdentity identity)
    {
        if (identity.UIndex > 0 && source.IsUExport(identity.UIndex) &&
            source.GetUExport(identity.UIndex) is { } indexed &&
            IsMaterialClass(indexed.ClassName) &&
            indexed.InstancedFullPath.Equals(identity.InstancedPath, StringComparison.OrdinalIgnoreCase))
        {
            return indexed;
        }
        var matches = source.Exports.Where(export =>
                IsMaterialClass(export.ClassName) &&
                export.InstancedFullPath.Equals(identity.InstancedPath, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidDataException(
                $"Material '{identity.InstancedPath}' was {(matches.Length == 0 ? "not found" : "ambiguous")} in '{source.FilePath}'.");
    }

    private static bool IsMaterialClass(string className) =>
        className.Equals("MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase) ||
        className.Equals("BioMaterialInstanceConstant", StringComparison.OrdinalIgnoreCase) ||
        className.Equals("Material", StringComparison.OrdinalIgnoreCase);

    private static MorphFaceMaterialData GetScopeData(
        MorphFaceMaterialData source,
        IReadOnlyDictionary<string, MorphFaceMaterialData> scopes,
        string scope)
    {
        if (scopes.TryGetValue(scope, out var scoped))
        {
            return scoped;
        }
        // Older detached documents were unscoped.  They are safe to consume
        // only when the request contains one parameter namespace.
        if (scopes.Count == 0)
        {
            return source;
        }
        return new MorphFaceMaterialData([], [], []);
    }

    private static IReadOnlyList<string> WriteMaterialParameters(
        IMEPackage destination,
        ExportEntry material,
        ResolvedHeadMaterial template,
        MorphFaceMaterialData source,
        IReadOnlyList<TextureCatalogCandidate> textureCatalog,
        bool preferBiogTextures,
        ICollection<string> warnings)
    {
        var names = new List<string>();
        var properties = material.GetProperties();
        var supportedScalars = source.Scalars
            .Where(value => IsSerializableMaterialParameter(value.Name) &&
                            template.Supports(value.Name, MaterialParameterKind.Scalar))
            .ToDictionary(value => value.Name, StringComparer.OrdinalIgnoreCase);
        var supportedVectors = source.Vectors
            .Where(value => IsSerializableMaterialParameter(value.Name) &&
                            template.Supports(value.Name, MaterialParameterKind.Vector))
            .ToDictionary(value => value.Name, StringComparer.OrdinalIgnoreCase);
        var supportedTextures = source.Textures
            .Where(value => IsSerializableMaterialParameter(value.Name) &&
                            template.Supports(value.Name, MaterialParameterKind.Texture))
            .ToDictionary(value => value.Name, StringComparer.OrdinalIgnoreCase);

        if (supportedScalars.Count > 0 || template.SupportedScalars.Count > 0)
        {
            var existing = properties.GetProp<ArrayProperty<StructProperty>>("ScalarParameterValues")?.ToList()
                           ?? new List<StructProperty>();
            var values = existing.Where(value =>
                    IsSerializableMaterialParameter(ParameterName(value)) &&
                    !supportedScalars.ContainsKey(ParameterName(value)))
                .ToList();
            foreach (var value in supportedScalars.Values)
            {
                values.Add(new StructProperty("ScalarParameterValue", false,
                    ExpressionGuid(),
                    new NameProperty(value.Name, "ParameterName"),
                    new FloatProperty(value.Value, "ParameterValue")));
                names.Add(value.Name);
            }
            properties.AddOrReplaceProp(new ArrayProperty<StructProperty>(values, "ScalarParameterValues"));
        }

        if (supportedVectors.Count > 0 || template.SupportedVectors.Count > 0)
        {
            var existing = properties.GetProp<ArrayProperty<StructProperty>>("VectorParameterValues")?.ToList()
                           ?? new List<StructProperty>();
            var values = existing.Where(value =>
                    IsSerializableMaterialParameter(ParameterName(value)) &&
                    !supportedVectors.ContainsKey(ParameterName(value)))
                .ToList();
            foreach (var value in supportedVectors.Values)
            {
                values.Add(new StructProperty("VectorParameterValue", false,
                    ExpressionGuid(),
                    new StructProperty("LinearColor", false,
                        new FloatProperty(value.Value.X, "R"),
                        new FloatProperty(value.Value.Y, "G"),
                        new FloatProperty(value.Value.Z, "B"),
                        new FloatProperty(value.Value.W, "A"))
                    { Name = "ParameterValue", IsImmutable = true },
                    new NameProperty(value.Name, "ParameterName")));
                names.Add(value.Name);
            }
            properties.AddOrReplaceProp(new ArrayProperty<StructProperty>(values, "VectorParameterValues"));
        }

        if (supportedTextures.Count > 0 || template.SupportedTextures.Count > 0)
        {
            var existing = properties.GetProp<ArrayProperty<StructProperty>>("TextureParameterValues")?.ToList()
                           ?? new List<StructProperty>();
            var values = existing.Where(value =>
                    IsSerializableMaterialParameter(ParameterName(value)) &&
                    !supportedTextures.ContainsKey(ParameterName(value)))
                .ToList();
            foreach (var value in supportedTextures.Values)
            {
                if (value.TextureReference is null)
                {
                    continue;
                }
                var texture = ResolveAuthoredTexture(
                    destination,
                    value.TextureReference,
                    textureCatalog,
                    preferBiogTextures,
                    warnings);
                values.Add(new StructProperty("TextureParameterValue", false,
                    ExpressionGuid(),
                    new NameProperty(value.Name, "ParameterName"),
                    new ObjectProperty(texture, "ParameterValue")));
                names.Add(value.Name);
            }
            properties.AddOrReplaceProp(new ArrayProperty<StructProperty>(values, "TextureParameterValues"));
        }

        material.WriteProperties(properties);
        return names;
    }

    private static IEntry ResolveAuthoredTexture(
        IMEPackage destination,
        AssetIdentity identity,
        IReadOnlyList<TextureCatalogCandidate> textureCatalog,
        bool preferBiogTextures,
        ICollection<string> warnings) => PccPackageWorkflow.ResolveTextureReference(
            destination,
            identity,
            textureCatalog,
            preferBiogTextures,
            warnings);

    private static string ParameterName(StructProperty value) =>
        value.GetProp<NameProperty>("ParameterName")?.Value.Instanced ?? string.Empty;

    internal static bool IsSerializableMaterialParameter(string name) =>
        !name.Trim().Equals("SelectionColor", StringComparison.OrdinalIgnoreCase);

    private static StructProperty ExpressionGuid()
    {
        var bytes = Guid.NewGuid().ToByteArray();
        return new StructProperty("Guid", false,
            new IntProperty(BitConverter.ToInt32(bytes, 0), "A"),
            new IntProperty(BitConverter.ToInt32(bytes, 4), "B"),
            new IntProperty(BitConverter.ToInt32(bytes, 8), "C"),
            new IntProperty(BitConverter.ToInt32(bytes, 12), "D"))
        { Name = "ExpressionGUID", IsImmutable = true };
    }

    private static string MakeMaterialName(string meshName, string materialName, ISet<string> used)
    {
        var baseName = $"{SanitizeName(meshName)}_{materialName}_MAT";
        var name = baseName;
        var suffix = 2;
        while (!used.Add(name))
        {
            name = $"{baseName}_{suffix++}";
        }
        return name;
    }

    private static string MaterialToken(CustomMaterialAssignmentOption option)
    {
        var scope = option.EffectiveParameterScopeKey.ToLowerInvariant();
        return (scope, option.Family) switch
        {
            ("human", HeadMaterialFamily.Skin) when
                IsHumanMale(option) => "HMMFace",
            ("human", HeadMaterialFamily.Skin) => "HMFFace",
            ("human", HeadMaterialFamily.Scalp) when
                IsHumanMale(option) => "HMMScalp",
            ("human", HeadMaterialFamily.Scalp) => "HMFScalp",
            ("human", HeadMaterialFamily.Eyes) when
                IsHumanMale(option) => "HMMEye",
            ("human", HeadMaterialFamily.Eyes) => "HMF-ASAEye",
            ("human", HeadMaterialFamily.Lashes) when
                IsHumanMale(option) => "HMMLash",
            ("human", HeadMaterialFamily.Lashes) => "HMF-ASALash",
            ("human", HeadMaterialFamily.Hair or HeadMaterialFamily.MaskedHair) when
                option.Id.Contains("human-iconic-femshep-hair", StringComparison.OrdinalIgnoreCase) => "IconicHair",
            ("human", HeadMaterialFamily.Hair or HeadMaterialFamily.MaskedHair) => "Hair",
            ("asari", HeadMaterialFamily.Skin or HeadMaterialFamily.AsariSkin) => "ASAFace",
            ("salarian", HeadMaterialFamily.Skin or HeadMaterialFamily.SalarianSkin) => "SALFace",
            ("salarian", HeadMaterialFamily.Eyes or HeadMaterialFamily.SalarianEyes) => "SALEye",
            ("turian", HeadMaterialFamily.Skin or HeadMaterialFamily.TurianSkin) => "TURFace",
            ("turian", HeadMaterialFamily.Eyes or HeadMaterialFamily.TurianEyes) => "TUREye",
            ("krogan", HeadMaterialFamily.Skin or HeadMaterialFamily.KroganSkin) => "KROFace",
            ("krogan", HeadMaterialFamily.Eyes or HeadMaterialFamily.KroganEyes) => "KROEye",
            ("batarian", HeadMaterialFamily.Skin or HeadMaterialFamily.BatarianSkin) => "BATFace",
            ("vorcha", HeadMaterialFamily.Skin or HeadMaterialFamily.VorchaSkin) => "ALNFace",
            ("vorcha", HeadMaterialFamily.Eyes or HeadMaterialFamily.VorchaEyes or HeadMaterialFamily.TurianEyes) => "ALNEye",
            _ => throw new InvalidDataException(
                $"Material '{option.Label}' has no PCC naming token for scope '{scope}' and family '{option.Family}'.")
        };
    }

    private static bool IsHumanMale(CustomMaterialAssignmentOption option) =>
        option.AppearanceCompatibilityKey.Equals("human-male", StringComparison.OrdinalIgnoreCase);

    private static string SanitizeName(string value)
    {
        var chars = value.Where(character => char.IsLetterOrDigit(character) || character == '_').ToArray();
        var result = chars.Length == 0 ? "Material" : new string(chars);
        return char.IsLetter(result[0]) || result[0] == '_' ? result : $"_{result}";
    }

    private static void Verify(
        string path,
        CustomMeshPccSaveRequest request,
        IReadOnlyList<CustomMeshPccMaterialResult> expectedMaterials,
        string expectedMeshPath,
        PccDependencyGraphSnapshot dependencyGraph)
    {
        using var package = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
        if (package.Game != ToMeGame(request.Game))
        {
            throw new InvalidDataException("The custom mesh PCC was written for the wrong game.");
        }
        var root = package.FindExport(PackageRootName, "Package")
                   ?? throw new InvalidDataException("The custom mesh PCC has no MorphFaceEditor package export.");
        var mesh = package.FindExport(expectedMeshPath, "SkeletalMesh")
                   ?? throw new InvalidDataException("The custom mesh PCC has no written SkeletalMesh export.");
        if (!ReferenceEquals(mesh.Parent, root))
        {
            throw new InvalidDataException("The written SkeletalMesh is outside the MorphFaceEditor package.");
        }
        foreach (var expected in expectedMaterials)
        {
            var material = package.FindExport(expected.MaterialPath)
                           ?? throw new InvalidDataException(
                               $"The written material '{expected.MaterialPath}' could not be reopened.");
            if (!ReferenceEquals(material.Parent, root))
            {
                throw new InvalidDataException(
                    $"The written material '{expected.MaterialPath}' is outside the MorphFaceEditor package.");
            }
        }
        PackageIntegrity.Verify(package);
        PccPackageWorkflow.VerifyDependencyGraph(package, dependencyGraph);
    }

    private static void ValidateRequest(CustomMeshPccSaveRequest request)
    {
        if (request.Game is not (MorphFaceGame.LE1 or MorphFaceGame.LE2 or MorphFaceGame.LE3))
        {
            throw new InvalidDataException($"Custom mesh PCC writing does not support {request.Game}.");
        }
        ValidateObjectName(request.MeshName);
        if (request.Workspace.Source != request.SourceMesh)
        {
            throw new InvalidOperationException("The custom workspace does not belong to the supplied mesh asset.");
        }
        if (request.Workspace.Assignments.Count == 0)
        {
            throw new InvalidDataException("At least one custom material assignment is required.");
        }
        if (request.Workspace.Assignments.Select(value => value.Slot.MaterialIndex).Distinct().Count() !=
            request.Workspace.Assignments.Count)
        {
            throw new InvalidDataException("Custom material assignments contain duplicate material slots.");
        }
    }

    private static void ValidateObjectName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !(char.IsLetter(value[0]) || value[0] == '_') ||
            value.Skip(1).Any(character => !(char.IsLetterOrDigit(character) || character == '_')))
        {
            throw new ArgumentException(
                "The mesh export name may contain letters, digits, and underscores, and cannot begin with a digit.",
                nameof(value));
        }
    }

    private static MEGame ToMeGame(MorphFaceGame game) => game switch
    {
        MorphFaceGame.LE1 => MEGame.LE1,
        MorphFaceGame.LE2 => MEGame.LE2,
        MorphFaceGame.LE3 => MEGame.LE3,
        _ => throw new InvalidDataException($"Unsupported source game '{game}'.")
    };

}
