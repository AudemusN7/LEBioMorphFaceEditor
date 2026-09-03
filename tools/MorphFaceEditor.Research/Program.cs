using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using System.Numerics;
using System.Text.Json;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Shaders;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.BinaryConverters.Shaders;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using MorphFaceEditor.LegendaryExplorer;

if (args.Length == 3 && args[0].Equals("corpus-audit", StringComparison.OrdinalIgnoreCase))
{
    LegendaryExplorerCoreRuntime.Initialize();
    var corpusDirectory = Path.GetFullPath(args[1]);
    var outputPath = Path.GetFullPath(args[2]);
    var packagePaths = Directory.EnumerateFiles(corpusDirectory, "*.pcc", SearchOption.TopDirectoryOnly)
        .Where(path => Path.GetFileName(path).Contains("GlobalMorphs", StringComparison.OrdinalIgnoreCase))
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    if (packagePaths.Length != 9)
    {
        throw new InvalidDataException(
            $"Expected the nine canonical GlobalMorphs packages in '{corpusDirectory}', but found {packagePaths.Length}.");
    }

    var audit = packagePaths.Select(AuditCorpusPackage).ToArray();
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    File.WriteAllText(outputPath, JsonSerializer.Serialize(audit, new JsonSerializerOptions
    {
        WriteIndented = true
    }), new UTF8Encoding(false));
    foreach (var auditedPackage in audit)
    {
        Console.WriteLine(
            $"{auditedPackage.FileName}: {auditedPackage.Game}; {auditedPackage.Faces.Count} faces; " +
            $"{auditedPackage.ReferencedAssets.Count} referenced assets; {auditedPackage.PackageStoredTextures.Count} package-stored textures");
    }
    Console.WriteLine($"Wrote {outputPath}");
    return 0;
}

if (args.Length == 3 && args[0].Equals("corpus-reconciliation", StringComparison.OrdinalIgnoreCase))
{
    LegendaryExplorerCoreRuntime.Initialize();
    var corpusDirectory = Path.GetFullPath(args[1]);
    var outputPath = Path.GetFullPath(args[2]);
    var audits = Directory.EnumerateFiles(corpusDirectory, "*.pcc", SearchOption.TopDirectoryOnly)
        .Where(path => Path.GetFileName(path).Contains("GlobalMorphs", StringComparison.OrdinalIgnoreCase))
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .Select(AuditCorpusPackage)
        .ToDictionary(value => value.FileName, StringComparer.OrdinalIgnoreCase);
    var pairs = new[]
    {
        ("LE1", "LE2"), ("LE1", "LE3"),
        ("LE2", "LE1"), ("LE2", "LE3"),
        ("LE3", "LE1"), ("LE3", "LE2")
    };
    var results = pairs.SelectMany(pair => BuildReconciliation(
            audits[$"{pair.Item1} GlobalMorphs.pcc"],
            audits[$"{pair.Item1} to {pair.Item2} GlobalMorphs.pcc"],
            pair.Item1,
            pair.Item2))
        .OrderBy(value => value.SourceGame, StringComparer.Ordinal)
        .ThenBy(value => value.TargetGame, StringComparer.Ordinal)
        .ThenBy(value => value.Kind, StringComparer.Ordinal)
        .ThenBy(value => value.Parameter, StringComparer.OrdinalIgnoreCase)
        .ThenBy(value => value.SourcePath, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    File.WriteAllText(outputPath, JsonSerializer.Serialize(results, new JsonSerializerOptions
    {
        WriteIndented = true
    }), new UTF8Encoding(false));
    Console.WriteLine($"Wrote {results.Length} unambiguous changed/omitted asset decisions to {outputPath}");
    return 0;
}

if (args.Length == 4 && args[0].Equals("actor-inventory", StringComparison.OrdinalIgnoreCase))
{
    var inventory = new ActorAssignmentInventoryService().Read(
        Path.GetFullPath(args[1]), args[2], args[3]);
    Console.WriteLine($"{inventory.Game} {inventory.SelectedFacePath} ({inventory.SelectedProfileKey})");
    Console.WriteLine($"Candidates: {inventory.Candidates.Count}; morph-ready: {inventory.Candidates.Count(value => value.CanAssignMorph)}; material-ready: {inventory.Candidates.Count(value => value.CanAssignMaterials)}");
    Console.WriteLine("Eligible material families:");
    foreach (var group in inventory.Candidates.SelectMany(value => value.MaterialTargets)
                 .GroupBy(value => value.Family).OrderBy(value => value.Key))
    {
        Console.WriteLine($"  {group.Key}: {group.Count()} slots / {group.Select(value => value.UIndex).Distinct().Count()} MICs");
    }
    Console.WriteLine("Skip reasons:");
    foreach (var group in inventory.Candidates.SelectMany(value => value.SkippedMaterials)
                 .GroupBy(value => value.Reason).OrderByDescending(value => value.Count()))
    {
        Console.WriteLine($"  {group.Count()}x {group.Key}");
    }
    Console.WriteLine("Unresolved/shared candidates:");
    foreach (var candidate in inventory.Candidates)
    {
        foreach (var skipped in candidate.SkippedMaterials.Where(value =>
                     value.ComponentRole == ActorComponentRole.Hair ||
                     value.Reason.Contains("not a recognized", StringComparison.OrdinalIgnoreCase) ||
                     value.Reason.Contains("not safe to infer", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine($"  {candidate.ObjectName} | {skipped.ComponentRole} {skipped.SlotIndex} | {skipped.Reason}");
            Console.WriteLine($"    {string.Join(" -> ", skipped.ParentChain.Select(value => value.InstancedPath))}");
        }
    }
    return 0;
}

if (args.Length == 3 && args[0].Equals("locate-export", StringComparison.OrdinalIgnoreCase))
{
    LegendaryExplorerCoreRuntime.Initialize();
    var root = Path.GetFullPath(args[1]);
    var exportName = args[2];
    var paths = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
        .Where(path => path.EndsWith(".pcc", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".upk", StringComparison.OrdinalIgnoreCase))
        .ToArray();
    var matches = new ConcurrentBag<string>();
    var failures = 0;
    var scanned = 0;
    Parallel.ForEach(paths, new ParallelOptions { MaxDegreeOfParallelism = 4 }, path =>
    {
        try
        {
            using var candidatePackage = MEPackageHandler.OpenMEPackage(path, forceLoadFromDisk: true);
            foreach (var export in candidatePackage.Exports.Where(export =>
                         export.ObjectName.Instanced.Contains(exportName, StringComparison.OrdinalIgnoreCase)))
            {
                matches.Add($"{path}\t#{export.UIndex}\t{export.ClassName}\t{export.InstancedFullPath}");
            }
        }
        catch
        {
            Interlocked.Increment(ref failures);
        }
        var completed = Interlocked.Increment(ref scanned);
        if (completed % 500 == 0)
        {
            Console.Error.WriteLine($"Scanned {completed}/{paths.Length} packages...");
        }
    });
    foreach (var match in matches.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine(match);
    }
    Console.Error.WriteLine($"Scanned {paths.Length} packages; {matches.Count} matches; {failures} unreadable.");
    return matches.IsEmpty ? 1 : 0;
}

if (args.Length == 3 && args[0].Equals("inventory-faces", StringComparison.OrdinalIgnoreCase))
{
    LegendaryExplorerCoreRuntime.Initialize();
    var facePackagePath = Path.GetFullPath(args[1]);
    var baseHeadFragment = args[2];
    using var facePackage = MEPackageHandler.OpenMEPackage(facePackagePath, forceLoadFromDisk: true);
    var faces = facePackage.Exports
        .Where(export => !export.IsDefaultObject &&
                         export.ClassName.Equals("BioMorphFace", StringComparison.OrdinalIgnoreCase))
        .Select(export =>
        {
            var properties = export.GetProperties();
            var baseHead = properties.GetProp<ObjectProperty>("m_oBaseHead")?.ResolveToEntry(facePackage);
            var hair = properties.GetProp<ObjectProperty>("m_oHairMesh")?.ResolveToEntry(facePackage);
            var otherMeshes = properties.GetProp<ArrayProperty<ObjectProperty>>("m_oOtherMeshes")?
                .Select(value => value.ResolveToEntry(facePackage)?.InstancedFullPath ?? "<unresolved>")
                .ToArray() ?? [];
            var features = properties.GetProp<ArrayProperty<StructProperty>>("m_aMorphFeatures")?
                .Select(value => (
                    Name: value.GetProp<NameProperty>("sFeatureName")?.Value.Instanced ?? "<unnamed>",
                    Offset: value.GetProp<FloatProperty>("Offset")?.Value ?? 0f))
                .ToArray() ?? [];
            var binary = export.GetBinaryData<BioMorphFace>();
            var lods = binary.LODs?.Select(value => value?.ToArray() ?? []).ToArray() ?? [];
            var basePositions = baseHead is ExportEntry baseHeadExport
                ? ReadLod0Positions(baseHeadExport)
                : [];
            return new
            {
                export,
                BaseHead = baseHead?.InstancedFullPath ?? "<unresolved>",
                Hair = hair?.InstancedFullPath ?? "<none>",
                OtherMeshes = otherMeshes,
                Features = features,
                LodVertexCounts = lods.Select(lod => lod.Length).ToArray(),
                Lod0Hash = lods.Length == 0 ? "<none>" : HashPositions(lods[0]),
                BaseVertexCount = basePositions.Length,
                BaseLod0MaximumDifference = lods.Length == 0
                    ? float.NaN
                    : MaximumDifference(basePositions, lods[0]),
                FinalSkeletonCount = properties.GetProp<ArrayProperty<StructProperty>>("m_aFinalSkeleton")?.Count ?? 0
            };
        })
        .Where(face => face.BaseHead.Contains(baseHeadFragment, StringComparison.OrdinalIgnoreCase))
        .OrderBy(face => face.export.InstancedFullPath, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    Console.WriteLine($"Game: {facePackage.Game}; package: {facePackagePath}; matching faces: {faces.Length}");
    foreach (var face in faces)
    {
        var nonZero = face.Features
            .Where(feature => Math.Abs(feature.Offset) >= 0.000001f)
            .Select(feature => $"{feature.Name}={feature.Offset:G9}");
        var allFeatures = face.Features
            .Select(feature => $"{feature.Name}={feature.Offset:G9}");
        Console.WriteLine($"#{face.export.UIndex}\t{face.export.InstancedFullPath}");
        Console.WriteLine($"  base={face.BaseHead}");
        Console.WriteLine($"  baked-lods=[{string.Join(", ", face.LodVertexCounts)}] lod0={face.Lod0Hash} base-vertices={face.BaseVertexCount} max-base-delta={face.BaseLod0MaximumDifference:G9}");
        Console.WriteLine($"  features-nonzero=[{string.Join(", ", nonZero)}]");
        Console.WriteLine($"  features-all=[{string.Join(", ", allFeatures)}]");
        Console.WriteLine($"  final-skeleton={face.FinalSkeletonCount} hair={face.Hair} other-meshes=[{string.Join(", ", face.OtherMeshes)}]");
    }
    return faces.Length == 0 ? 1 : 0;
}

if (args.Length == 6 && args[0].Equals("dump-shaders", StringComparison.OrdinalIgnoreCase))
{
    LegendaryExplorerCoreRuntime.Initialize();
    var sourcePackagePath = Path.GetFullPath(args[1]);
    var sourceMeshName = args[2];
    var materialSlot = int.Parse(args[3]);
    var vertexFactoryName = args[4];
    var outputDirectory = Path.GetFullPath(args[5]);
    using var sourcePackage = MEPackageHandler.OpenMEPackage(sourcePackagePath, forceLoadFromDisk: true);
    var sourceMeshExport = sourcePackage.Exports.Single(export =>
        export.ClassName.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase)
        && export.ObjectName.Instanced.Equals(sourceMeshName, StringComparison.OrdinalIgnoreCase));
    var sourceMesh = ObjectBinary.From<SkeletalMesh>(sourceMeshExport);
    if (materialSlot < 0 || materialSlot >= sourceMesh.Materials.Length)
    {
        throw new ArgumentOutOfRangeException(nameof(materialSlot));
    }
    using var sourceCache = new PackageCache();
    var sourceMaterial = Resolve(sourcePackage.GetEntry(sourceMesh.Materials[materialSlot]), sourceCache)
        ?? throw new InvalidDataException("The selected material slot could not be resolved.");
    if (sourceMeshExport.InstancedFullPath.EndsWith(
            "BIOG_ALN_HED_PROMorph_R.ALN_HED_PROBase_MDL", StringComparison.OrdinalIgnoreCase) &&
        sourceMaterial.ClassName.StartsWith("MaterialExpression", StringComparison.OrdinalIgnoreCase))
    {
        var expectedPath = materialSlot switch
        {
            0 => "BIOG_ALN_HED_PROMorph_R.PROBase.ALN_HED_PROBASE_MAT_1a",
            1 => "BIOG_ALN_HED_PROMorph_R.ALN_EYE_MAT_1a",
            _ => null
        };
        sourceMaterial = expectedPath is null
            ? sourceMaterial
            : sourcePackage.Exports.FirstOrDefault(export =>
                export.InstancedFullPath.Equals(expectedPath, StringComparison.OrdinalIgnoreCase) &&
                export.ClassName.Equals("MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase))
              ?? sourceMaterial;
    }
    var sourceChain = ReadMaterialChain(sourceMaterial, sourceCache);
    var shaderOwner = sourceChain.Select(value => value.Entry).First(entry =>
        entry.ClassName.Equals("Material", StringComparison.OrdinalIgnoreCase));
    var (sourceMap, _) = ShaderCacheManipulator.GetMaterialShaderMapAndShaders(shaderOwner);
    var meshMap = sourceMap.MeshShaderMaps.Single(value =>
        value.VertexFactoryType.Instanced.Equals(vertexFactoryName, StringComparison.Ordinal));
    var shaderTypes = meshMap.Shaders.Keys.Select(value => value.Instanced).Distinct(StringComparer.Ordinal).ToArray();
    var shaderGuids = shaderTypes.Select(shaderType =>
        meshMap.Shaders.First(value => value.Key.Instanced.Equals(shaderType, StringComparison.Ordinal)).Value.Id).ToArray();
    var sourceShaders = new Shader?[shaderGuids.Length];
    if (shaderOwner.FileRef.FindExport("SeekFreeShaderCache", "ShaderCache") is { } localCacheExport)
    {
        var localCache = ObjectBinary.From<ShaderCache>(localCacheExport);
        for (var index = 0; index < shaderGuids.Length; index++)
        {
            localCache.Shaders.TryGetValue(shaderGuids[index], out sourceShaders[index]);
        }
    }
    var referenceShaders = RefShaderCacheReader.GetShaders(shaderOwner.Game, shaderGuids, out _, out _);
    if (referenceShaders is not null)
    {
        for (var index = 0; index < shaderGuids.Length; index++)
        {
            sourceShaders[index] ??= referenceShaders[index];
        }
    }
    Directory.CreateDirectory(outputDirectory);
    var manifest = new StringBuilder();
    manifest.AppendLine("shaderType\tguid\tfrequency\tinstructions\tbytes\tsha256\tfile");
    for (var index = 0; index < shaderTypes.Length; index++)
    {
        var shader = sourceShaders[index];
        if (shader is null)
        {
            continue;
        }
        var safeName = string.Concat(shaderTypes[index].Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) || character is '<' or '>' ? '_' : character));
        var stem = $"{index:D3}-{safeName}-{shader.Guid:N}";
        var bytecodePath = Path.Combine(outputDirectory, stem + ".bin");
        var assemblyPath = Path.Combine(outputDirectory, stem + ".asm");
        File.WriteAllBytes(bytecodePath, shader.ShaderByteCode);
        using (var bytecode = new SharpDX.D3DCompiler.ShaderBytecode(shader.ShaderByteCode))
        {
            File.WriteAllText(assemblyPath, bytecode.Disassemble(), new UTF8Encoding(false));
        }
        manifest.AppendLine(string.Join('\t',
            shaderTypes[index], shader.Guid, shader.Frequency, shader.InstructionCount,
            shader.ShaderByteCode.Length, Convert.ToHexString(SHA256.HashData(shader.ShaderByteCode)),
            Path.GetFileName(assemblyPath)));
    }
    File.WriteAllText(Path.Combine(outputDirectory, "manifest.tsv"), manifest.ToString(), new UTF8Encoding(false));
    Console.WriteLine($"Dumped {sourceShaders.Count(shader => shader is not null)} shaders to {outputDirectory}");
    return 0;
}

if (args.Length is not 4 || !args[0].Equals("trace-material", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Usage: corpus-audit <corpus-directory> <output.json>");
    Console.Error.WriteLine("   or: corpus-reconciliation <corpus-directory> <output.json>");
    Console.Error.WriteLine("   or: locate-export <game-root> <export-name>");
    Console.Error.WriteLine("   or: inventory-faces <package.pcc> <base-head-name-fragment>");
    Console.Error.WriteLine("   or: actor-inventory <package.pcc> <face-selector> <profile-key>");
    Console.Error.WriteLine("   or: trace-material <package.pcc> <skeletal-mesh-name> <report.md>");
    Console.Error.WriteLine("   or: dump-shaders <package.pcc> <skeletal-mesh-name> <slot> <vertex-factory> <output-directory>");
    return 2;
}

LegendaryExplorerCoreRuntime.Initialize();
var packagePath = Path.GetFullPath(args[1]);
var meshName = args[2];
var reportPath = Path.GetFullPath(args[3]);

using var package = MEPackageHandler.OpenMEPackage(packagePath, forceLoadFromDisk: true);
var meshExport = package.Exports.SingleOrDefault(export =>
    export.ClassName.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase)
    && export.ObjectName.Instanced.Equals(meshName, StringComparison.OrdinalIgnoreCase));
if (meshExport is null)
{
    Console.Error.WriteLine($"SkeletalMesh '{meshName}' was not found in {packagePath}.");
    foreach (var candidate in package.Exports.Where(export =>
                 export.ClassName.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase)
                 || export.ObjectName.Instanced.Contains("PROShort", StringComparison.OrdinalIgnoreCase)))
    {
        Console.Error.WriteLine($"  #{candidate.UIndex} {candidate.ClassName} {candidate.InstancedFullPath}");
    }
    return 1;
}

using var cache = new PackageCache();
var mesh = ObjectBinary.From<SkeletalMesh>(meshExport);
var report = new StringBuilder();
report.AppendLine("# Material shader trace");
report.AppendLine();
report.AppendLine("> Pass 1 records package/material provenance and compiled shader-map structure only. It intentionally does not extract, disassemble, or interpret shader bytecode.");
report.AppendLine();
report.AppendLine("## Source");
report.AppendLine();
report.AppendLine($"- Game: `{package.Game}`");
report.AppendLine($"- Package: `{Escape(packagePath)}`");
report.AppendLine($"- Package SHA-256: `{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(packagePath)))}`");
report.AppendLine($"- Mesh: `#{meshExport.UIndex} {Escape(meshExport.InstancedFullPath)}`");
report.AppendLine($"- Material slots: `{mesh.Materials.Length}`");
report.AppendLine();

for (var slot = 0; slot < mesh.Materials.Length; slot++)
{
    var reference = package.GetEntry(mesh.Materials[slot]);
    var material = Resolve(reference, cache);
    report.AppendLine($"## Material slot {slot}");
    report.AppendLine();
    report.AppendLine($"- Stored reference: `{Describe(reference)}`");
    report.AppendLine($"- Resolved export: `{Describe(material)}`");
    report.AppendLine();
    if (material is null)
    {
        continue;
    }

    var chain = ReadMaterialChain(material, cache);
    report.AppendLine("### Parent chain");
    report.AppendLine();
    foreach (var (entry, properties) in chain)
    {
        var hasStaticPermutation = properties.GetProp<BoolProperty>("bHasStaticPermutationResource")?.Value;
        report.AppendLine($"- `{Describe(entry)}`; static permutation: `{hasStaticPermutation?.ToString() ?? "n/a"}`; data size: `{entry.DataSize}`");
    }
    report.AppendLine();

    report.AppendLine("### Authored overrides and bindings");
    report.AppendLine();
    foreach (var (entry, properties) in chain)
    {
        report.AppendLine($"#### `{Escape(entry.ObjectName.Instanced)}`");
        report.AppendLine();
        WriteParameterArray(report, entry, properties, "ScalarParameterValues");
        WriteParameterArray(report, entry, properties, "VectorParameterValues");
        WriteParameterArray(report, entry, properties, "TextureParameterValues");
        if (entry.ClassName.Equals("Material", StringComparison.OrdinalIgnoreCase))
        {
            WriteMaterialDefinition(report, entry, properties);
        }
    }

    foreach (var (candidate, _) in chain)
    {
        try
        {
            var (shaderMap, _) = ShaderCacheManipulator.GetMaterialShaderMapAndShaders(candidate);
            report.AppendLine("### Compiled shader map");
            report.AppendLine();
            report.AppendLine($"- Owner: `{Describe(candidate)}`");
            report.AppendLine($"- Friendly name: `{Escape(shaderMap.FriendlyName)}`");
            report.AppendLine($"- Shader map ID: `{shaderMap.ID}`");
            report.AppendLine($"- Base material ID: `{shaderMap.StaticParameters.BaseMaterialId}`");
            report.AppendLine($"- Static switches: `{shaderMap.StaticParameters.StaticSwitchParameters?.Length ?? 0}`");
            report.AppendLine($"- Static component masks: `{shaderMap.StaticParameters.StaticComponentMaskParameters?.Length ?? 0}`");
            report.AppendLine();
            WriteUniforms(report, "Pixel vectors", shaderMap.UniformPixelVectorExpressions);
            WriteUniforms(report, "Pixel scalars", shaderMap.UniformPixelScalarExpressions);
            WriteUniforms(report, "2D textures", shaderMap.Uniform2DTextureExpressions);
            WriteUniforms(report, "Cube textures", shaderMap.UniformCubeTextureExpressions);
            report.AppendLine("#### Mesh shader maps");
            report.AppendLine();
            foreach (var vertexFactory in shaderMap.MeshShaderMaps.OrderBy(value => value.VertexFactoryType.Instanced, StringComparer.Ordinal))
            {
                report.AppendLine($"- `{Escape(vertexFactory.VertexFactoryType.Instanced)}` ({vertexFactory.Shaders.Count} shaders)");
                foreach (var shader in vertexFactory.Shaders.OrderBy(value => value.Key.Instanced, StringComparer.Ordinal))
                {
                    report.AppendLine($"  - `{Escape(shader.Key.Instanced)}` → `{shader.Value.Id}`");
                }
            }
            report.AppendLine();
            break;
        }
        catch (Exception exception)
        {
            report.AppendLine($"- Shader-map probe failed for `{Describe(candidate)}`: `{Escape(exception.Message)}`");
        }
    }
}

Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
Console.WriteLine($"Wrote {reportPath}");
return 0;

static List<(ExportEntry Entry, PropertyCollection Properties)> ReadMaterialChain(ExportEntry material, PackageCache cache)
{
    var result = new List<(ExportEntry, PropertyCollection)>();
    var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    ExportEntry? current = material;
    while (current is not null && visited.Add($"{current.FileRef.FilePath}|{current.UIndex}"))
    {
        var properties = current.GetProperties(packageCache: cache);
        result.Add((current, properties));
        if (current.ClassName.Equals("Material", StringComparison.OrdinalIgnoreCase))
        {
            break;
        }

        var parentName = current.IsA("RvrEffectsMaterialUser") ? "m_pBaseMaterial" : "Parent";
        current = Resolve(properties.GetProp<ObjectProperty>(parentName)?.ResolveToEntry(current.FileRef), cache);
    }
    return result;
}

static ExportEntry? Resolve(IEntry? entry, PackageCache cache) => entry switch
{
    ExportEntry export => export,
    ImportEntry import => TryResolveImport(import, cache),
    _ => null
};

static ExportEntry? TryResolveImport(ImportEntry import, PackageCache cache)
{
    try
    {
        return EntryImporter.ResolveImport(import, cache);
    }
    catch
    {
        return null;
    }
}

static void WriteParameterArray(StringBuilder report, ExportEntry owner, PropertyCollection properties, string propertyName)
{
    var values = properties.GetProp<ArrayProperty<StructProperty>>(propertyName);
    if (values is null || values.Count == 0)
    {
        return;
    }

    report.AppendLine($"- `{propertyName}`:");
    foreach (var item in values)
    {
        var name = item.GetProp<NameProperty>("ParameterName")?.Value.Instanced ?? "<unnamed>";
        var scalar = item.GetProp<FloatProperty>("ParameterValue");
        var vector = item.GetProp<StructProperty>("ParameterValue");
        var texture = item.GetProp<ObjectProperty>("ParameterValue");
        var value = scalar is not null
            ? scalar.Value.ToString("G9")
            : vector is not null
                ? string.Join(", ", vector.Properties.Select(FormatSimpleProperty))
                : texture is not null
                    ? Describe(texture.ResolveToEntry(owner.FileRef))
                    : "<unread>";
        report.AppendLine($"  - `{Escape(name)}` = `{Escape(value)}`");
    }
    report.AppendLine();
}

static void WriteMaterialDefinition(StringBuilder report, ExportEntry materialExport, PropertyCollection properties)
{
    report.AppendLine("- Cooked material properties:");
    foreach (var property in properties.Where(property =>
                 !property.Name.Instanced.Equals("Expressions", StringComparison.OrdinalIgnoreCase)))
    {
        report.AppendLine($"  - `{Escape(property.Name.Instanced)}` = `{Escape(FormatProperty(property, materialExport.FileRef))}`");
    }
    report.AppendLine();

    IReadOnlyList<ObjectProperty> expressions =
        properties.GetProp<ArrayProperty<ObjectProperty>>("Expressions")?.ToArray() ?? [];
    report.AppendLine($"- Retained expression exports: `{expressions.Count}`");
    foreach (var expressionReference in expressions)
    {
        report.AppendLine($"  - `{Escape(Describe(expressionReference.ResolveToEntry(materialExport.FileRef)))}`");
    }
    report.AppendLine();

    var resource = ObjectBinary.From<Material>(materialExport).SM3MaterialResource;
    report.AppendLine($"- Resource flags: scene colour `{resource.bUsesSceneColor}`, scene depth `{resource.bUsesSceneDepth}`, dynamic parameter `{resource.bUsesDynamicParameter}`, lightmap UVs `{resource.bUsesLightmapUVs}`, vertex-position offset `{resource.bUsesMaterialVertexPositionOffset}`");
    report.AppendLine($"- User texture coordinates: `{resource.NumUserTexCoords}`; coordinate transforms: `0x{resource.UsingTransforms:X8}`");
    report.AppendLine("- Uniform expression texture table:");
    for (var index = 0; index < resource.UniformExpressionTextures.Length; index++)
    {
        report.AppendLine($"  - `[{index}] {Escape(Describe(materialExport.FileRef.GetEntry(resource.UniformExpressionTextures[index])))}`");
    }
    report.AppendLine();
}

static void WriteUniforms(StringBuilder report, string title, IEnumerable<MaterialUniformExpression>? expressions)
{
    var values = expressions?.ToArray() ?? [];
    report.AppendLine($"#### {title} ({values.Length})");
    report.AppendLine();
    for (var index = 0; index < values.Length; index++)
    {
        report.AppendLine($"- `[{index}] {Escape(DescribeUniform(values[index]))}`");
    }
    report.AppendLine();
}

static string DescribeUniform(MaterialUniformExpression expression) => expression switch
{
    MaterialUniformExpressionScalarParameter scalar => $"{expression.ExpressionType.Instanced}: {scalar.ParameterName.Instanced}",
    MaterialUniformExpressionVectorParameter vector => $"{expression.ExpressionType.Instanced}: {vector.ParameterName.Instanced}",
    MaterialUniformExpressionTextureParameter texture => $"{expression.ExpressionType.Instanced}: {texture.ParameterName.Instanced} (texture index {texture.TextureIndex})",
    MaterialUniformExpressionTexture texture => $"{expression.ExpressionType.Instanced} (texture index {texture.TextureIndex})",
    _ => expression.ExpressionType.Instanced
};

static string FormatSimpleProperty(Property property) => property switch
{
    FloatProperty value => $"{value.Name.Instanced}={value.Value:G9}",
    IntProperty value => $"{value.Name.Instanced}={value.Value}",
    BoolProperty value => $"{value.Name.Instanced}={value.Value}",
    NameProperty value => $"{value.Name.Instanced}={value.Value.Instanced}",
    _ => $"{property.Name.Instanced}={property}"
};

static string FormatProperty(Property property, IMEPackage package) => property switch
{
    ObjectProperty value => Describe(value.ResolveToEntry(package)),
    NameProperty value => value.Value.Instanced,
    EnumProperty value => value.Value.Instanced,
    BoolProperty value => value.Value.ToString(),
    FloatProperty value => value.Value.ToString("G9"),
    IntProperty value => value.Value.ToString(),
    ByteProperty value => value.Value.ToString(),
    StrProperty value => value.Value,
    StructProperty value => $"{value.StructType} {{{string.Join(", ", value.Properties.Select(child => $"{child.Name.Instanced}={FormatProperty(child, package)}"))}}}",
    ArrayProperty<ObjectProperty> value => string.Join(", ", value.Select(item => Describe(item.ResolveToEntry(package)))),
    ArrayProperty<StructProperty> value => string.Join(" | ", value.Select(item => FormatProperty(item, package))),
    _ => property.ToString() ?? string.Empty
};

static string Describe(IEntry? entry) => entry is null
    ? "<unresolved>"
    : $"{entry.FileRef.FilePath} | #{entry.UIndex} {entry.ClassName} {entry.InstancedFullPath}";

static string Escape(string? value) => (value ?? string.Empty).Replace("`", "'").Replace("\r", " ").Replace("\n", " ");

static string HashPositions(IReadOnlyList<Vector3> positions)
{
    var bytes = new byte[positions.Count * sizeof(float) * 3];
    for (var index = 0; index < positions.Count; index++)
    {
        var offset = index * sizeof(float) * 3;
        BitConverter.TryWriteBytes(bytes.AsSpan(offset), positions[index].X);
        BitConverter.TryWriteBytes(bytes.AsSpan(offset + sizeof(float)), positions[index].Y);
        BitConverter.TryWriteBytes(bytes.AsSpan(offset + sizeof(float) * 2), positions[index].Z);
    }
    return Convert.ToHexString(SHA256.HashData(bytes));
}

static Vector3[] ReadLod0Positions(ExportEntry meshExport) =>
    meshExport.GetBinaryData<SkeletalMesh>().LODModels?[0].VertexBufferGPUSkin?.VertexData?
        .Select(vertex => vertex.Position)
        .ToArray() ?? [];

static float MaximumDifference(IReadOnlyList<Vector3> left, IReadOnlyList<Vector3> right)
{
    if (left.Count != right.Count)
    {
        return float.PositiveInfinity;
    }
    var maximum = 0f;
    for (var index = 0; index < left.Count; index++)
    {
        maximum = Math.Max(maximum, Vector3.Distance(left[index], right[index]));
    }
    return maximum;
}

static CorpusPackageAudit AuditCorpusPackage(string packagePath)
{
    using var package = MEPackageHandler.OpenMEPackage(packagePath, forceLoadFromDisk: true);
    var faces = package.Exports
        .Where(export => !export.IsDefaultObject &&
                         export.ClassName.Equals("BioMorphFace", StringComparison.OrdinalIgnoreCase))
        .OrderBy(export => export.InstancedFullPath, StringComparer.OrdinalIgnoreCase)
        .Select(face =>
        {
            var properties = face.GetProperties();
            var materialOverride = properties.GetProp<ObjectProperty>("m_oMaterialOverrides")?.ResolveToEntry(package)
                                   as ExportEntry;
            var textureOverrides = materialOverride?.GetProperty<ArrayProperty<StructProperty>>(
                    "m_aTextureOverrides")?
                .Select(value => new CorpusTextureOverride(
                    value.GetProp<NameProperty>("nName")?.Value.Instanced ?? "<unnamed>",
                    DescribeCorpusEntry(value.GetProp<ObjectProperty>("m_pTexture")?.ResolveToEntry(package))))
                .OrderBy(value => value.Parameter, StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? [];
            return new CorpusFaceAudit(
                face.InstancedFullPath,
                DescribeCorpusEntry(properties.GetProp<ObjectProperty>("m_oBaseHead")?.ResolveToEntry(package)),
                DescribeCorpusEntry(properties.GetProp<ObjectProperty>("m_oHairMesh")?.ResolveToEntry(package)),
                properties.GetProp<ArrayProperty<ObjectProperty>>("m_oOtherMeshes")?
                    .Select(value => DescribeCorpusEntry(value.ResolveToEntry(package)))
                    .ToArray() ?? [],
                materialOverride?.InstancedFullPath,
                textureOverrides);
        })
        .ToArray();
    var referencedAssets = faces
        .SelectMany(face => new[] { face.BaseHead, face.Hair }
            .Concat(face.OtherMeshes)
            .Concat(face.TextureOverrides.Select(value => value.Texture)))
        .Where(value => value is not null)
        .Cast<CorpusEntryAudit>()
        .DistinctBy(value => $"{value.Kind}|{value.ClassName}|{value.Path}", StringComparer.OrdinalIgnoreCase)
        .OrderBy(value => value.ClassName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(value => value.Path, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    var storedTextures = package.Exports
        .Where(export => export.ClassName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase))
        .Select(export =>
        {
            var texture = new LegendaryExplorerCore.Unreal.Classes.Texture2D(export);
            return new CorpusStoredTexture(
                export.InstancedFullPath,
                texture.GetTopMip().IsPackageStored,
                DescribeCorpusEntry(export.Parent));
        })
        .Where(value => value.IsPackageStored)
        .OrderBy(value => value.Path, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    return new CorpusPackageAudit(
        Path.GetFileName(packagePath),
        package.Game.ToString(),
        faces,
        referencedAssets,
        storedTextures);
}

static CorpusEntryAudit? DescribeCorpusEntry(IEntry? entry) => entry is null
    ? null
    : new CorpusEntryAudit(
        entry is ExportEntry ? "Export" : "Import",
        entry.ClassName,
        entry.InstancedFullPath,
        entry.UIndex,
        entry.Parent is null ? null : $"{(entry.Parent is ExportEntry ? "Export" : "Import")}:{entry.Parent.InstancedFullPath}");

static IEnumerable<CorpusReconciliationDecision> BuildReconciliation(
    CorpusPackageAudit source,
    CorpusPackageAudit target,
    string sourceGame,
    string targetGame)
{
    var targetFaces = target.Faces.ToDictionary(value => value.Path, StringComparer.OrdinalIgnoreCase);
    var evidence = new List<(string Kind, string? Parameter, string SourcePath, string? TargetPath)>();
    foreach (var sourceFace in source.Faces)
    {
        if (!targetFaces.TryGetValue(sourceFace.Path, out var targetFace))
        {
            continue;
        }
        if (sourceFace.Hair is not null)
        {
            evidence.Add(("Hair", null, sourceFace.Hair.Path, targetFace.Hair?.Path));
        }
        for (var index = 0; index < sourceFace.OtherMeshes.Count; index++)
        {
            if (sourceFace.OtherMeshes[index] is { } other)
            {
                evidence.Add(("Other", null, other.Path,
                    index < targetFace.OtherMeshes.Count ? targetFace.OtherMeshes[index]?.Path : null));
            }
        }
        var targetTextures = targetFace.TextureOverrides
            .GroupBy(value => value.Parameter, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Texture?.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var texture in sourceFace.TextureOverrides.Where(value => value.Texture is not null))
        {
            targetTextures.TryGetValue(texture.Parameter, out var targetPath);
            evidence.Add(("Texture", texture.Parameter, texture.Texture!.Path, targetPath));
        }
    }

    foreach (var group in evidence.GroupBy(value =>
                 $"{value.Kind}\u001f{value.Parameter}\u001f{value.SourcePath}", StringComparer.OrdinalIgnoreCase))
    {
        var targets = group.Select(value => value.TargetPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (targets.Length != 1)
        {
            Console.Error.WriteLine(
                $"Skipped ambiguous {sourceGame}->{targetGame} reconciliation for {group.Key}: " +
                string.Join(", ", targets.Select(value => value ?? "<omitted>")));
            continue;
        }
        var sample = group.First();
        if (targets[0] is not null &&
            targets[0]!.Equals(sample.SourcePath, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }
        yield return new CorpusReconciliationDecision(
            sourceGame,
            targetGame,
            sample.Kind,
            sample.Parameter,
            sample.SourcePath,
            targets[0],
            group.Count());
    }
}

sealed record CorpusPackageAudit(
    string FileName,
    string Game,
    IReadOnlyList<CorpusFaceAudit> Faces,
    IReadOnlyList<CorpusEntryAudit> ReferencedAssets,
    IReadOnlyList<CorpusStoredTexture> PackageStoredTextures);

sealed record CorpusFaceAudit(
    string Path,
    CorpusEntryAudit? BaseHead,
    CorpusEntryAudit? Hair,
    IReadOnlyList<CorpusEntryAudit?> OtherMeshes,
    string? MaterialOverride,
    IReadOnlyList<CorpusTextureOverride> TextureOverrides);

sealed record CorpusTextureOverride(string Parameter, CorpusEntryAudit? Texture);
sealed record CorpusEntryAudit(string Kind, string ClassName, string Path, int UIndex, string? Parent);
sealed record CorpusStoredTexture(string Path, bool IsPackageStored, CorpusEntryAudit? Parent);
sealed record CorpusReconciliationDecision(
    string SourceGame,
    string TargetGame,
    string Kind,
    string? Parameter,
    string SourcePath,
    string? TargetPath,
    int EvidenceCount);
