using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Text.Json;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Core.Domain;
using SharpGLTF.Schema2;
using SharpGLTF.Validation;
using BinaryMorphFace = LegendaryExplorerCore.Unreal.BinaryConverters.BioMorphFace;
using BinarySkeletalMesh = LegendaryExplorerCore.Unreal.BinaryConverters.SkeletalMesh;

namespace MorphFaceEditor.LegendaryExplorer;

public enum MorphMeshFormat
{
    Psk,
    Gltf,
    Md5
}

public sealed record MorphMeshExportResult(
    MorphMeshFormat Format,
    IReadOnlyList<string> ProducedFiles,
    string ToolOutput);

/// <summary>
/// Owns external mesh interchange: LEC/UModel export and position-only parsing
/// for the three formats accepted by the inverse morph solver.
/// </summary>
public sealed class MorphFaceInterchangeService
{
    private const int MinimumUModelBuild = 1589;

    public async Task<MorphMeshExportResult> ExportMeshAsync(
        string packagePath,
        string facePath,
        string outputDirectory,
        MorphMeshFormat format,
        CancellationToken cancellationToken = default)
    {
        LegendaryExplorerCoreRuntime.Initialize();
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var executable = UModelPath();
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException(
                "UModel is not installed. Run Legendary Explorer's mesh export once so it can install the supported UModel build, then retry.",
                executable);
        }
        var build = await GetUModelBuildAsync(executable, cancellationToken);
        if (build < MinimumUModelBuild)
        {
            throw new InvalidOperationException(
                $"Legendary Explorer's UModel build is {build}; build {MinimumUModelBuild} or newer is required.");
        }

        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"MorphFaceEditor.Export.{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var tempPackagePath = CreateAppliedMeshPackage(
                packagePath,
                facePath,
                temporaryDirectory,
                out var meshName,
                out var bakedPositions,
                out var baseHeadPath);
            var sourceMorph = new MorphFacePackageContextService().CaptureMorphData(packagePath, facePath);
            var fitPrior = new MorphMeshFitPrior(
                baseHeadPath,
                sourceMorph.MorphFeatures,
                sourceMorph.FinalSkeleton,
                sourceMorph.BakedLods);
            var arguments = new List<string> { "-export", "-lods" };
            if (format != MorphMeshFormat.Psk)
            {
                arguments.Add(format == MorphMeshFormat.Gltf ? "-gltf" : "-md5");
            }
            arguments.Add($"-out={outputDirectory}");
            arguments.Add(tempPackagePath);
            arguments.Add(meshName);
            arguments.Add("SkeletalMesh");
            var run = await RunAsync(executable, arguments, cancellationToken);
            if (run.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"UModel exited with code {run.ExitCode}. {LastUsefulLine(run.Output)}");
            }

            var extensions = format switch
            {
                MorphMeshFormat.Psk => new[] { ".psk", ".pskx" },
                MorphMeshFormat.Gltf => new[] { ".gltf", ".bin" },
                MorphMeshFormat.Md5 => new[] { ".md5mesh", ".md5anim" },
                _ => throw new ArgumentOutOfRangeException(nameof(format))
            };
            var produced = Directory.EnumerateFiles(outputDirectory, "*", SearchOption.AllDirectories)
                .Where(path => extensions.Contains(
                               Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) &&
                               IsMeshLodFile(path, meshName))
                .ToArray();
            if (!produced.Any(path => IsPrimaryExtension(path, format)))
            {
                throw new InvalidDataException(
                    $"UModel completed but did not produce a {format} mesh. {LastUsefulLine(run.Output)}");
            }
            var primary = produced.First(path => IsPrimaryExtension(path, format));
            var sidecar = TryWriteVertexMapSidecar(primary, format, bakedPositions, fitPrior);
            if (sidecar is null && format == MorphMeshFormat.Gltf)
            {
                var mappingOutput = Path.Combine(temporaryDirectory, "md5-map");
                Directory.CreateDirectory(mappingOutput);
                var mappingRun = await RunAsync(executable,
                [
                    "-export", "-lods", "-md5", $"-out={mappingOutput}",
                    tempPackagePath, meshName, "SkeletalMesh"
                ], cancellationToken);
                if (mappingRun.ExitCode == 0)
                {
                    var md5Path = Directory.EnumerateFiles(
                            mappingOutput, "*.md5mesh", SearchOption.AllDirectories)
                        .FirstOrDefault();
                    if (md5Path is not null)
                    {
                        sidecar = TryWriteGltfVertexMapSidecar(
                            primary, md5Path, bakedPositions, fitPrior);
                    }
                }
            }
            sidecar ??= WritePriorOnlySidecar(primary + ".mfe.json", fitPrior);
            return new MorphMeshExportResult(
                format,
                sidecar is null ? produced : [.. produced, sidecar],
                run.Output);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    public IReadOnlyList<MorphMeshPositionCandidate> ReadMeshPositions(string path, SkeletalMeshAsset baseHead)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Imported morph mesh was not found.", path);
        }
        var expectedVertexCount = baseHead.Positions.Length;
        var candidates = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".psk" or ".pskx" => ReadPsk(path, expectedVertexCount),
            ".gltf" or ".glb" => ReadGltf(path, baseHead),
            ".md5" or ".md5mesh" => ReadMd5(path, baseHead),
            _ => throw new NotSupportedException(
                "Morph mesh import supports PSK/PSKX, glTF/GLB, and MD5/MD5Mesh files.")
        };
        return ApplyVertexMapSidecar(path, candidates, expectedVertexCount);
    }

    internal static string CreateAppliedMeshPackage(
        string packagePath,
        string facePath,
        string temporaryDirectory,
        out string meshName,
        out Vector3[] bakedPositions,
        out string baseHeadPath)
    {
        using var sourcePackage = MEPackageHandler.OpenMEPackage(packagePath, forceLoadFromDisk: true);
        var face = sourcePackage.Exports.SingleOrDefault(export =>
                       export.ClassName == "BioMorphFace" &&
                       export.InstancedFullPath.Equals(facePath, StringComparison.OrdinalIgnoreCase))
                   ?? throw new KeyNotFoundException($"BioMorphFace '{facePath}' was not found in the open PCC.");
        var baseHeadReference = face.GetProperty<ObjectProperty>("m_oBaseHead")?.ResolveToEntry(sourcePackage)
                                ?? throw new InvalidDataException(
                                    $"BioMorphFace '{facePath}' has no resolvable base head.");
        using var packageCache = new PackageCache();
        var baseHeadExport = new GamePackageReferenceResolver(packageCache).Require(
            baseHeadReference,
            $"BioMorphFace '{facePath}' base head");
        baseHeadPath = baseHeadExport.InstancedFullPath;
        var appliedHead = baseHeadExport.GetBinaryData<BinarySkeletalMesh>()
                          ?? throw new InvalidDataException(
                              $"BioMorphFace '{facePath}' base head has no SkeletalMesh binary.");
        var morphBinary = face.GetBinaryData<BinaryMorphFace>();
        if (morphBinary.LODs is not { Length: > 0 } bakedLods || bakedLods[0].Length == 0)
        {
            throw new InvalidDataException($"BioMorphFace '{facePath}' has no baked mesh LODs.");
        }

        var tempPackagePath = Path.Combine(temporaryDirectory, "MorphFaceExport.pcc");
        MEPackageHandler.CreateAndSavePackage(tempPackagePath, face.Game);
        using var temporaryPackage = MEPackageHandler.OpenMEPackage(tempPackagePath, forceLoadFromDisk: true);
        bakedPositions = bakedLods[0].ToArray();

        for (var lodIndex = 0; lodIndex < Math.Min(
                 bakedLods.Length,
                 appliedHead.LODModels.Length); lodIndex++)
        {
            var bakedLod = bakedLods[lodIndex];
            var vertices = appliedHead.LODModels[lodIndex].VertexBufferGPUSkin.VertexData;
            if (bakedLod.Length != vertices.Length)
            {
                if (lodIndex > 0)
                {
                    // Some shipped faces retain a lower BioMorphFace LOD whose
                    // topology does not match the base mesh's same-numbered LOD.
                    // It cannot be staged in UModel, but version 3 sidecars carry
                    // it losslessly for an unchanged export/import round trip.
                    continue;
                }
                throw new InvalidDataException(
                    $"BioMorphFace '{facePath}' LOD {lodIndex} has {bakedLod.Length} vertices; " +
                    $"its base head has {vertices.Length}.");
            }
            for (var vertexIndex = 0; vertexIndex < vertices.Length; vertexIndex++)
            {
                var vertex = vertices[vertexIndex];
                vertex.Position = bakedLod[vertexIndex];
                vertices[vertexIndex] = vertex;
            }
        }

        // UModel only needs the SkeletalMesh binary. Cloning the base head's full
        // dependency graph also clones MICs whose texture parameters can retain
        // valid negative import UIndexes. UModel 1589 then tries to load those
        // imports as exports and aborts before reaching the mesh. Keep the staging
        // package geometry-only and preserve the number of material slots as nulls.
        Array.Fill(appliedHead.Materials, 0);
        var clonedHead = ExportCreator.CreateExport(
            temporaryPackage,
            face.ObjectName,
            "SkeletalMesh",
            indexed: false);
        clonedHead.WriteProperty(MeshHelper.GetLodInfoForSkeletalMesh(appliedHead, face.Game));
        clonedHead.WriteBinary(appliedHead);
        meshName = clonedHead.ObjectNameString;
        temporaryPackage.Save(tempPackagePath);
        return tempPackagePath;
    }

    private static string? TryWriteVertexMapSidecar(
        string meshPath,
        MorphMeshFormat format,
        IReadOnlyList<Vector3> bakedPositions,
        MorphMeshFitPrior fitPrior)
    {
        if (format == MorphMeshFormat.Psk)
        {
            return null;
        }
        var baseHead = CreatePositionOnlyMesh(bakedPositions);
        var candidates = format == MorphMeshFormat.Gltf
            ? ReadGltf(meshPath, baseHead)
            : ReadMd5(meshPath, baseHead);
        foreach (var candidate in candidates.Where(value =>
                     value.CoordinateSystem.EndsWith("file order", StringComparison.Ordinal)))
        {
            if (!TryMatchExactVertexSet(candidate.Positions, bakedPositions, out var exportedToBase))
            {
                continue;
            }
            var sidecarPath = meshPath + ".mfe.json";
            return WriteSidecar(sidecarPath, candidate, exportedToBase, bakedPositions, fitPrior);
        }
        return null;
    }

    private static string? TryWriteGltfVertexMapSidecar(
        string gltfPath,
        string md5Path,
        IReadOnlyList<Vector3> bakedPositions,
        MorphMeshFitPrior fitPrior)
    {
        var baseHead = CreatePositionOnlyMesh(bakedPositions);
        var md5Candidates = ReadMd5(md5Path, baseHead);
        int[]? exportedToBase = null;
        foreach (var candidate in md5Candidates.Where(value =>
                     value.CoordinateSystem.EndsWith("file order", StringComparison.Ordinal)))
        {
            if (TryMatchExactVertexSet(candidate.Positions, bakedPositions, out var mapping))
            {
                exportedToBase = mapping;
                break;
            }
        }
        if (exportedToBase is null)
        {
            return null;
        }

        var gltfCandidate = ReadGltf(gltfPath, baseHead)
            .Where(value => value.CoordinateSystem.EndsWith("file order", StringComparison.Ordinal) &&
                            value.Positions.Count == exportedToBase.Length)
            .MinBy(value => Enumerable.Range(0, exportedToBase.Length).Sum(index =>
                Vector3.DistanceSquared(value.Positions[index], bakedPositions[exportedToBase[index]])));
        return gltfCandidate is null
            ? null
            : WriteSidecar(gltfPath + ".mfe.json", gltfCandidate, exportedToBase, bakedPositions, fitPrior);
    }

    private static string WriteSidecar(
        string sidecarPath,
        MorphMeshPositionCandidate candidate,
        int[] exportedToBase,
        IReadOnlyList<Vector3> bakedPositions,
        MorphMeshFitPrior fitPrior)
    {
        var corrections = exportedToBase.SelectMany((baseIndex, exportedIndex) =>
        {
            var value = bakedPositions[baseIndex] - candidate.Positions[exportedIndex];
            return new[] { value.X, value.Y, value.Z };
        }).ToArray();
        WriteSidecarFile(sidecarPath, new VertexMapSidecar(
            3,
            candidate.CoordinateSystem,
            exportedToBase,
            corrections,
            fitPrior.BaseHeadPath,
            fitPrior.MorphFeatures.Select(value =>
                new SidecarMorphFeature(value.Name, value.Offset)).ToArray(),
            fitPrior.FinalSkeleton.Select(value => new SidecarBoneTranslation(
                value.BoneName,
                value.Translation.X,
                value.Translation.Y,
                value.Translation.Z)).ToArray(),
            EncodeBakedLods(fitPrior.BakedLods)));
        return sidecarPath;
    }

    private static string WritePriorOnlySidecar(string sidecarPath, MorphMeshFitPrior fitPrior)
    {
        WriteSidecarFile(sidecarPath, new VertexMapSidecar(
            3,
            string.Empty,
            [],
            null,
            fitPrior.BaseHeadPath,
            fitPrior.MorphFeatures.Select(value =>
                new SidecarMorphFeature(value.Name, value.Offset)).ToArray(),
            fitPrior.FinalSkeleton.Select(value => new SidecarBoneTranslation(
                value.BoneName,
                value.Translation.X,
                value.Translation.Y,
                value.Translation.Z)).ToArray(),
            EncodeBakedLods(fitPrior.BakedLods)));
        return sidecarPath;
    }

    private static void WriteSidecarFile(string path, VertexMapSidecar sidecar) =>
        File.WriteAllText(path, JsonSerializer.Serialize(
            sidecar,
            new JsonSerializerOptions { WriteIndented = true }));

    private static float[][] EncodeBakedLods(IReadOnlyList<Vector3[]> bakedLods) =>
        bakedLods.Select(lod => lod.SelectMany(value => new[]
        {
            value.X,
            value.Y,
            value.Z
        }).ToArray()).ToArray();

    private static Vector3[][] DecodeBakedLods(IReadOnlyList<float[]> bakedLods) =>
        bakedLods.Select((lod, lodIndex) =>
        {
            if (lod.Length % 3 != 0)
            {
                throw new InvalidDataException(
                    $"Mesh sidecar baked LOD {lodIndex} does not contain XYZ triplets.");
            }
            return Enumerable.Range(0, lod.Length / 3)
                .Select(index => new Vector3(lod[index * 3], lod[index * 3 + 1], lod[index * 3 + 2]))
                .ToArray();
        }).ToArray();

    private static IReadOnlyList<MorphMeshPositionCandidate> ApplyVertexMapSidecar(
        string meshPath,
        IReadOnlyList<MorphMeshPositionCandidate> candidates,
        int expectedVertexCount)
    {
        var sidecarPath = meshPath + ".mfe.json";
        if (!File.Exists(sidecarPath))
        {
            return candidates;
        }
        try
        {
            var sidecar = JsonSerializer.Deserialize<VertexMapSidecar>(File.ReadAllText(sidecarPath));
            var prior = sidecar is
                { Version: 3, BaseHeadPath: not null, MorphFeatures: not null, FinalSkeleton: not null, BakedLods: not null }
                ? new MorphMeshFitPrior(
                    sidecar.BaseHeadPath,
                    sidecar.MorphFeatures.Select(value =>
                        new MorphFeatureValue(value.Name, value.Offset)).ToArray(),
                    sidecar.FinalSkeleton.Select(value => new BoneTranslation(
                        value.BoneName,
                        new Vector3(value.X, value.Y, value.Z))).ToArray(),
                    DecodeBakedLods(sidecar.BakedLods))
                : null;
            var candidatesWithPrior = candidates
                .Select(candidate => candidate with { Prior = prior })
                .ToArray();
            var source = sidecar is { Version: 1 or 2 or 3 } &&
                         sidecar.ExportedToBase.Length == expectedVertexCount
                ? candidatesWithPrior.FirstOrDefault(value =>
                    value.CoordinateSystem.Equals(sidecar.CoordinateSystem, StringComparison.Ordinal))
                : null;
            if (source is null || source.Positions.Count != expectedVertexCount ||
                sidecar!.ExportedToBase.Distinct().Count() != expectedVertexCount ||
                sidecar.ExportedToBase.Any(index => index < 0 || index >= expectedVertexCount))
            {
                return candidatesWithPrior;
            }
            var reordered = new Vector3[expectedVertexCount];
            for (var exportedIndex = 0; exportedIndex < expectedVertexCount; exportedIndex++)
            {
                var correction = sidecar.Corrections is { } corrections &&
                                 corrections.Length == expectedVertexCount * 3
                    ? new Vector3(
                        corrections[exportedIndex * 3],
                        corrections[exportedIndex * 3 + 1],
                        corrections[exportedIndex * 3 + 2])
                    : Vector3.Zero;
                reordered[sidecar.ExportedToBase[exportedIndex]] =
                    source.Positions[exportedIndex] + correction;
            }
            return
            [
                new MorphMeshPositionCandidate(
                    $"{source.CoordinateSystem}, MFE vertex map",
                    reordered,
                    prior),
                .. candidatesWithPrior
            ];
        }
        catch (JsonException)
        {
            return candidates;
        }
    }

    private static bool TryMatchExactVertexSet(
        IReadOnlyList<Vector3> exported,
        IReadOnlyList<Vector3> baked,
        out int[] exportedToBase)
    {
        exportedToBase = [];
        if (exported.Count != baked.Count)
        {
            return false;
        }
        const float tolerance = 0.001f;
        var available = Enumerable.Range(0, baked.Count).ToHashSet();
        var mapping = new int[exported.Count];
        for (var exportedIndex = 0; exportedIndex < exported.Count; exportedIndex++)
        {
            var nearest = -1;
            var nearestDistance = tolerance * tolerance;
            foreach (var baseIndex in available)
            {
                var distance = Vector3.DistanceSquared(exported[exportedIndex], baked[baseIndex]);
                if (distance <= nearestDistance)
                {
                    nearest = baseIndex;
                    nearestDistance = distance;
                    if (distance == 0) break;
                }
            }
            if (nearest < 0)
            {
                return false;
            }
            mapping[exportedIndex] = nearest;
            available.Remove(nearest);
        }
        exportedToBase = mapping;
        return true;
    }

    private static SkeletalMeshAsset CreatePositionOnlyMesh(IReadOnlyList<Vector3> positions)
    {
        var topology = new SkeletalMeshTopology(
            0, positions.Count, 0, 0, [], [], [], [], [], string.Empty);
        return new SkeletalMeshAsset(
            new AssetIdentity(string.Empty, "UModelExport", 0, "SkeletalMesh"),
            positions.ToArray(),
            new Vector3[positions.Count],
            topology);
    }

    private static IReadOnlyList<MorphMeshPositionCandidate> ReadPsk(string path, int expectedVertexCount)
    {
        var psk = PSK.FromFile(path);
        // UModel collapses identical point positions but retains one wedge per GPU
        // vertex. Re-expand through PointIndex to restore BioMorphFace vertex order.
        var points = psk.Points
            .Select(value => new Vector3(value.X, -value.Y, value.Z))
            .ToArray();
        var positions = points.Length == expectedVertexCount
            ? points
            : psk.Wedges.Count == expectedVertexCount
                ? psk.Wedges.Select(wedge => points[wedge.PointIndex]).ToArray()
                : points;
        return [new MorphMeshPositionCandidate("PSK (Y handedness restored)", positions)];
    }

    private static IReadOnlyList<MorphMeshPositionCandidate> ReadGltf(string path, SkeletalMeshAsset baseHead)
    {
        var expectedVertexCount = baseHead.Positions.Length;
        // UModel build 1589 occasionally rounds accessor min/max metadata one ULP
        // inward. The binary position data is valid, so bypass schema bounds checks.
        var model = ModelRoot.Load(path, new ReadSettings { Validation = ValidationMode.Skip });
        var streams = model.LogicalMeshes
            .SelectMany(mesh => mesh.Primitives)
            .Select(primitive => new VertexStream(
                primitive.GetVertexAccessor("POSITION")?.AsVector3Array().ToArray() ?? [],
                primitive.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array().ToArray() ?? []))
            .Where(stream => stream.Positions.Length > 0)
            .DistinctBy(stream => stream.Positions)
            .ToArray();
        var exact = streams.Where(stream => stream.Positions.Length == expectedVertexCount).ToArray();
        IReadOnlyList<VertexStream> viable = exact.Length > 0
            ? exact
            : streams.Sum(stream => stream.Positions.Length) == expectedVertexCount
                ? [new VertexStream(
                    streams.SelectMany(stream => stream.Positions).ToArray(),
                    streams.SelectMany(stream => stream.TextureCoordinates).ToArray())]
                : streams;
        return viable.SelectMany((stream, index) => CoordinateCandidates(
                stream.Positions,
                stream.TextureCoordinates,
                $"glTF stream {index + 1}",
                scale: 100,
                baseHead))
            .ToArray();
    }

    private static IReadOnlyList<MorphMeshPositionCandidate> ReadMd5(string path, SkeletalMeshAsset baseHead)
    {
        var expectedVertexCount = baseHead.Positions.Length;
        var source = File.ReadAllText(path);
        var jointsBlock = ExtractBlocks(source, "joints").SingleOrDefault()
                          ?? throw new InvalidDataException("MD5 mesh has no joints block.");
        var joints = JointRegex.Matches(jointsBlock)
            .Select(match => new Md5Joint(
                int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                ReadVector(match.Groups[3].Value),
                ReadQuaternion(match.Groups[4].Value)))
            .ToArray();
        if (joints.Length == 0)
        {
            throw new InvalidDataException("MD5 mesh contains no parseable joints.");
        }

        var streams = new List<VertexStream>();
        foreach (var meshBlock in ExtractBlocks(source, "mesh"))
        {
            var vertices = VertRegex.Matches(meshBlock)
                .Select(match => new Md5Vertex(
                    ReadVector2(match.Groups[1].Value),
                    int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                    int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture)))
                .ToArray();
            var weights = WeightRegex.Matches(meshBlock)
                .Select(match => new Md5Weight(
                    int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                    float.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture),
                    ReadVector(match.Groups[4].Value)))
                .ToArray();
            if (vertices.Length == 0)
            {
                continue;
            }
            var positions = vertices.Select(vertex =>
            {
                var result = Vector3.Zero;
                for (var index = vertex.FirstWeight; index < vertex.FirstWeight + vertex.WeightCount; index++)
                {
                    if ((uint)index >= (uint)weights.Length || (uint)weights[index].Joint >= (uint)joints.Length)
                    {
                        throw new InvalidDataException("MD5 vertex references an invalid weight or joint.");
                    }
                    var weight = weights[index];
                    var joint = joints[weight.Joint];
                    result += (joint.Position + Vector3.Transform(weight.Position, joint.Orientation)) * weight.Bias;
                }
                return result;
            }).ToArray();
            streams.Add(new VertexStream(positions, vertices.Select(value => value.TextureCoordinate).ToArray()));
        }
        var viable = streams.Where(stream => stream.Positions.Length == expectedVertexCount).ToList();
        if (viable.Count == 0 && streams.Sum(stream => stream.Positions.Length) == expectedVertexCount)
        {
            viable.Add(new VertexStream(
                streams.SelectMany(stream => stream.Positions).ToArray(),
                streams.SelectMany(stream => stream.TextureCoordinates).ToArray()));
        }
        if (viable.Count == 0)
        {
            viable.AddRange(streams);
        }
        return viable.SelectMany((stream, index) => CoordinateCandidates(
                stream.Positions,
                stream.TextureCoordinates,
                $"MD5 mesh {index + 1}",
                scale: 1,
                baseHead))
            .ToArray();
    }

    private static IEnumerable<MorphMeshPositionCandidate> CoordinateCandidates(
        Vector3[] source,
        Vector2[] textureCoordinates,
        string label,
        float scale,
        SkeletalMeshAsset baseHead)
    {
        var transforms = new (string Name, Func<Vector3, Vector3> Apply)[]
        {
            ("XYZ", value => value * scale),
            ("X-YZ", value => new Vector3(value.X, -value.Y, value.Z) * scale),
            ("XZ-Y", value => new Vector3(value.X, value.Z, -value.Y) * scale),
            ("X-ZY", value => new Vector3(value.X, -value.Z, value.Y) * scale)
        };
        foreach (var transform in transforms)
        {
            var positions = source.Select(transform.Apply).ToArray();
            if (TryReorderByTextureCoordinates(
                    positions, textureCoordinates, baseHead, flipV: false, out var reordered))
            {
                yield return new MorphMeshPositionCandidate($"{label}, {transform.Name}, UV order", reordered);
            }
            if (TryReorderByTextureCoordinates(
                    positions, textureCoordinates, baseHead, flipV: true, out reordered))
            {
                yield return new MorphMeshPositionCandidate($"{label}, {transform.Name}, flipped-V order", reordered);
            }
            yield return new MorphMeshPositionCandidate($"{label}, {transform.Name}, file order", positions);
        }
    }

    private static bool TryReorderByTextureCoordinates(
        IReadOnlyList<Vector3> importedPositions,
        IReadOnlyList<Vector2> importedTextureCoordinates,
        SkeletalMeshAsset baseHead,
        bool flipV,
        out Vector3[] reordered)
    {
        reordered = [];
        var renderData = baseHead.FindLod(0)?.RenderData ?? baseHead.RenderData;
        if (renderData is null ||
            importedPositions.Count != baseHead.Positions.Length ||
            importedTextureCoordinates.Count != importedPositions.Count ||
            renderData.TextureCoordinates.Count != baseHead.Positions.Length)
        {
            return false;
        }

        static (int U, int V) Key(Vector2 value) =>
            ((int)MathF.Round(value.X * 65536), (int)MathF.Round(value.Y * 65536));
        var destinationGroups = renderData.TextureCoordinates
            .Select((value, index) => (Key: Key(value), Index: index))
            .GroupBy(value => value.Key)
            .ToDictionary(group => group.Key, group => group.Select(value => value.Index).ToList());
        var sourceGroups = importedTextureCoordinates
            .Select((value, index) =>
            {
                var normalized = flipV ? new Vector2(value.X, 1 - value.Y) : value;
                return (Key: Key(normalized), Index: index);
            })
            .GroupBy(value => value.Key)
            .ToArray();
        if (sourceGroups.Sum(group => group.Count()) != baseHead.Positions.Length)
        {
            return false;
        }

        reordered = new Vector3[baseHead.Positions.Length];
        foreach (var sourceGroup in sourceGroups)
        {
            if (!destinationGroups.TryGetValue(sourceGroup.Key, out var destinations) ||
                destinations.Count != sourceGroup.Count())
            {
                reordered = [];
                return false;
            }
            var available = destinations.ToList();
            foreach (var sourceIndex in sourceGroup.Select(value => value.Index))
            {
                var destination = available.MinBy(index =>
                    Vector3.DistanceSquared(baseHead.Positions[index], importedPositions[sourceIndex]));
                reordered[destination] = importedPositions[sourceIndex];
                available.Remove(destination);
            }
        }
        return true;
    }

    private static IReadOnlyList<string> ExtractBlocks(string source, string keyword)
    {
        var blocks = new List<string>();
        var search = 0;
        while ((search = source.IndexOf(keyword, search, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var opening = source.IndexOf('{', search + keyword.Length);
            if (opening < 0)
            {
                break;
            }
            var depth = 1;
            var index = opening + 1;
            while (index < source.Length && depth > 0)
            {
                if (source[index] == '{') depth++;
                if (source[index] == '}') depth--;
                index++;
            }
            if (depth != 0)
            {
                throw new InvalidDataException($"MD5 {keyword} block is not terminated.");
            }
            blocks.Add(source[(opening + 1)..(index - 1)]);
            search = index;
        }
        return blocks;
    }

    private static Vector3 ReadVector(string value)
    {
        var numbers = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(item => float.Parse(item, CultureInfo.InvariantCulture)).ToArray();
        if (numbers.Length != 3)
        {
            throw new InvalidDataException("MD5 vector does not have three components.");
        }
        return new Vector3(numbers[0], numbers[1], numbers[2]);
    }

    private static Vector2 ReadVector2(string value)
    {
        var numbers = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(item => float.Parse(item, CultureInfo.InvariantCulture)).ToArray();
        if (numbers.Length != 2)
        {
            throw new InvalidDataException("MD5 texture coordinate does not have two components.");
        }
        return new Vector2(numbers[0], numbers[1]);
    }

    private static Quaternion ReadQuaternion(string value)
    {
        var vector = ReadVector(value);
        var w = -MathF.Sqrt(MathF.Max(0, 1 - vector.LengthSquared()));
        return Quaternion.Normalize(new Quaternion(vector, w));
    }

    private static async Task<int> GetUModelBuildAsync(string executable, CancellationToken cancellationToken)
    {
        var result = await RunAsync(executable, ["-version"], cancellationToken);
        var match = Regex.Match(result.Output, @"Compiled .*? (\d+)\)", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var build) ? build : 0;
    }

    private static async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)
                            ?? throw new InvalidOperationException("UModel could not be started.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = string.Join(Environment.NewLine,
            new[] { await outputTask, await errorTask }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return new ProcessResult(process.ExitCode, output);
    }

    private static string UModelPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LegendaryExplorer",
        "staticexecutables",
        "umodel",
        "umodel.exe");

    private static string LastUsefulLine(string output) => output
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .LastOrDefault()?.Trim() ?? "No diagnostic output was returned.";

    private static bool IsPrimaryExtension(string path, MorphMeshFormat format) =>
        format switch
        {
            MorphMeshFormat.Psk => Path.GetExtension(path).Equals(".psk", StringComparison.OrdinalIgnoreCase) ||
                                   Path.GetExtension(path).Equals(".pskx", StringComparison.OrdinalIgnoreCase),
            MorphMeshFormat.Gltf => Path.GetExtension(path).Equals(".gltf", StringComparison.OrdinalIgnoreCase),
            MorphMeshFormat.Md5 => Path.GetExtension(path).Equals(".md5mesh", StringComparison.OrdinalIgnoreCase),
            _ => false
        };

    private static bool IsMeshLodFile(string path, string meshName)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.Equals(meshName, StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith(meshName + "_Lod", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly Regex JointRegex = new(
        "\\\"([^\\\"]+)\\\"\\s+(-?\\d+)\\s+\\(([^)]+)\\)\\s+\\(([^)]+)\\)",
        RegexOptions.Compiled);
    private static readonly Regex VertRegex = new(
        @"\bvert\s+\d+\s+\(([^)]+)\)\s+(\d+)\s+(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex WeightRegex = new(
        @"\bweight\s+(\d+)\s+(\d+)\s+([-+\d.eE]+)\s+\(([^)]+)\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private sealed record ProcessResult(int ExitCode, string Output);
    private sealed record VertexMapSidecar(
        int Version,
        string CoordinateSystem,
        int[] ExportedToBase,
        float[]? Corrections = null,
        string? BaseHeadPath = null,
        SidecarMorphFeature[]? MorphFeatures = null,
        SidecarBoneTranslation[]? FinalSkeleton = null,
        float[][]? BakedLods = null);
    private sealed record SidecarMorphFeature(string Name, float Offset);
    private sealed record SidecarBoneTranslation(string BoneName, float X, float Y, float Z);
    private sealed record Md5Joint(int Parent, Vector3 Position, Quaternion Orientation);
    private sealed record VertexStream(Vector3[] Positions, Vector2[] TextureCoordinates);
    private sealed record Md5Vertex(Vector2 TextureCoordinate, int FirstWeight, int WeightCount);
    private sealed record Md5Weight(int Joint, float Bias, Vector3 Position);
}
