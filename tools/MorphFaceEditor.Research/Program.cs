using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using System.Numerics;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Shaders;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.BinaryConverters.Shaders;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using MorphFaceEditor.LegendaryExplorer;

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
    Console.Error.WriteLine("Usage: locate-export <game-root> <export-name>");
    Console.Error.WriteLine("   or: inventory-faces <package.pcc> <base-head-name-fragment>");
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
