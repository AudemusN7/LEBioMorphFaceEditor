using System.Numerics;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.LegendaryExplorer;

namespace MorphFaceEditor.DataCompiler;

public sealed record VorchaReconstructionResult(
    IReadOnlyList<MorphTargetAsset> Le2Targets,
    IReadOnlyList<MorphTargetAsset> Le3Targets,
    int OracleFaceCount,
    int FeatureCount,
    float MaximumLe2PositionError,
    float MaximumLe2BoneError,
    IReadOnlyList<int> Le3CompatibleLods);

/// <summary>
/// Reconstructs a Vorcha target basis from the six shipped LE2 baked-face
/// oracles. The game contains feature weights but no ALN MorphTargetSet, so
/// the solution is the minimum-norm linear basis that exactly recreates every
/// known baked mesh and final skeleton. It is intentionally provenance-marked
/// as reconstructed rather than treated as an extracted game asset.
/// </summary>
public static class VorchaMorphReconstructor
{
    private const string BaseHeadMarker = "ALN_HED_PROBase_MDL";
    // Matches the editor's baked-face oracle tolerance. Float32 target storage
    // accumulates a few ULPs across the 17 reconstructed controls.
    private const float VerificationTolerance = 0.0001f;

    public static VorchaReconstructionResult Reconstruct(string le2PackagePath, string le3PackagePath)
    {
        LegendaryExplorerCoreRuntime.Initialize();
        var le2 = ReadCorpus(le2PackagePath, requireSamples: true);
        var le3 = ReadCorpus(le3PackagePath, requireSamples: false);

        var featureNames = le2.Samples[0].Features.Keys
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (le2.Samples.Count != 6)
        {
            throw new InvalidDataException(
                $"Expected six LE2 Vorcha faces with geometry controls; found {le2.Samples.Count}.");
        }
        if (featureNames.Length != 17 || le2.Samples.Any(face =>
                !face.Features.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .SequenceEqual(featureNames, StringComparer.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                "The LE2 Vorcha oracle faces do not expose one consistent 17-feature control surface.");
        }

        var weights = new double[le2.Samples.Count, featureNames.Length];
        for (var face = 0; face < le2.Samples.Count; face++)
        {
            for (var feature = 0; feature < featureNames.Length; feature++)
            {
                weights[face, feature] = le2.Samples[face].Features[featureNames[feature]];
            }
        }
        var pseudoInverse = MinimumNormPseudoInverse(weights);
        var le2CompatibleLods = CompatibleOracleLods(le2);
        if (!le2CompatibleLods.Contains(0))
        {
            throw new InvalidDataException("LE2 Vorcha LOD 0 is not compatible with its baked-face oracle.");
        }
        var le2Targets = BuildTargets(le2, featureNames, pseudoInverse, le2CompatibleLods);
        var (maximumPositionError, maximumBoneError) = VerifyLe2Oracles(
            le2, featureNames, le2Targets);
        if (maximumPositionError > VerificationTolerance || maximumBoneError > VerificationTolerance)
        {
            throw new InvalidDataException(
                "The reconstructed Vorcha target basis did not reproduce its LE2 baked-face oracle within tolerance. " +
                $"Position error {maximumPositionError:G9}; bone error {maximumBoneError:G9}.");
        }

        var compatibleLods = le3.BaseLods
            .Select((positions, index) => (Positions: positions, Index: index))
            .Where(value => le2CompatibleLods.Contains(value.Index) &&
                            value.Index < le2.BaseLods.Count &&
                            value.Positions.Length == le2.BaseLods[value.Index].Length &&
                            MaximumDifference(value.Positions, le2.BaseLods[value.Index]) <= VerificationTolerance)
            .Select(value => value.Index)
            .ToArray();
        if (compatibleLods.Length == 0)
        {
            throw new InvalidDataException(
                "LE3 Vorcha has no topology-identical LOD on which to apply the LE2 reconstruction.");
        }
        var le3Targets = BuildTargets(le2, featureNames, pseudoInverse, compatibleLods)
            .Select(target => target with
            {
                Source = target.Source with { PackagePath = "generated://vorcha-le3-reconstruction" }
            })
            .ToArray();

        return new VorchaReconstructionResult(
            le2Targets,
            le3Targets,
            le2.Samples.Count,
            featureNames.Length,
            maximumPositionError,
            maximumBoneError,
            compatibleLods);
    }

    private static Corpus ReadCorpus(string packagePath, bool requireSamples)
    {
        var fullPath = Path.GetFullPath(packagePath);
        using var package = MEPackageHandler.OpenMEPackage(fullPath, forceLoadFromDisk: true);
        var faces = package.Exports
            .Where(export => !export.IsDefaultObject &&
                             export.ClassName.Equals("BioMorphFace", StringComparison.OrdinalIgnoreCase))
            .Select(ReadFace)
            .Where(face => face.BaseHead.InstancedFullPath.Contains(BaseHeadMarker, StringComparison.OrdinalIgnoreCase))
            .OrderBy(face => face.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (faces.Length == 0)
        {
            throw new InvalidDataException($"No Vorcha BioMorphFaces were found in '{fullPath}'.");
        }

        var baseHead = faces[0].BaseHead;
        if (faces.Any(face => !face.BaseHead.InstancedFullPath.Equals(
                baseHead.InstancedFullPath, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("Vorcha corpus faces do not share one base head.");
        }
        var mesh = baseHead.GetBinaryData<SkeletalMesh>();
        var baseLods = mesh.LODModels?
            .Select(lod => lod.VertexBufferGPUSkin?.VertexData?
                .Select(vertex => vertex.Position)
                .ToArray() ?? [])
            .ToArray() ?? [];
        if (baseLods.Length == 0 || baseLods[0].Length == 0)
        {
            throw new InvalidDataException($"Vorcha base head '{baseHead.InstancedFullPath}' has no readable GPU-skin LODs.");
        }
        var baseBones = mesh.RefSkeleton?
            .ToDictionary(bone => bone.Name.Instanced, bone => bone.Position, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);
        var zeroFaces = faces.Where(face => face.Features.Values.All(value => Math.Abs(value) < 0.000001f)).ToArray();
        if (zeroFaces.Length == 0 || zeroFaces.Any(face =>
                face.BakedLods.Count == 0 ||
                MaximumDifference(face.BakedLods[0], baseLods[0]) > VerificationTolerance))
        {
            throw new InvalidDataException(
                "The Vorcha corpus has no zero-feature baked face matching the base head; reconstruction has no verified intercept.");
        }
        var samples = faces.Where(face => face.Features.Values.Any(value => Math.Abs(value) >= 0.000001f)).ToArray();
        if (requireSamples && samples.Length == 0)
        {
            throw new InvalidDataException("The LE2 Vorcha corpus contains no non-zero feature samples.");
        }
        return new Corpus(baseLods, baseBones, samples);
    }

    private static CorpusFace ReadFace(ExportEntry export)
    {
        var properties = export.GetProperties();
        var baseHead = properties.GetProp<ObjectProperty>("m_oBaseHead")?.ResolveToEntry(export.FileRef) as ExportEntry
            ?? throw new InvalidDataException($"BioMorphFace '{export.InstancedFullPath}' has no local Vorcha base mesh.");
        var features = properties.GetProp<ArrayProperty<StructProperty>>("m_aMorphFeatures")?
            .Select(value => (
                Name: value.GetProp<NameProperty>("sFeatureName")?.Value.Instanced ?? string.Empty,
                Offset: value.GetProp<FloatProperty>("Offset")?.Value ?? 0f))
            .Where(value => !string.IsNullOrWhiteSpace(value.Name))
            .ToDictionary(value => value.Name, value => value.Offset, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        var bones = properties.GetProp<ArrayProperty<StructProperty>>("m_aFinalSkeleton")?
            .Select(value => (
                Name: value.GetProp<NameProperty>("nName")?.Value.Instanced ?? string.Empty,
                Position: ReadVector(value.GetProp<StructProperty>("vPos"))))
            .Where(value => !string.IsNullOrWhiteSpace(value.Name))
            .ToDictionary(value => value.Name, value => value.Position, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);
        var baked = export.GetBinaryData<BioMorphFace>().LODs?
            .Select(lod => lod?.ToArray() ?? [])
            .ToArray() ?? [];
        return new CorpusFace(export.InstancedFullPath, baseHead, features, bones, baked);
    }

    private static IReadOnlyList<MorphTargetAsset> BuildTargets(
        Corpus corpus,
        IReadOnlyList<string> featureNames,
        double[,] pseudoInverse,
        IEnumerable<int> lodIndices)
    {
        var targets = new List<MorphTargetAsset>(featureNames.Count);
        foreach (var (featureName, featureIndex) in featureNames.Select((value, index) => (value, index)))
        {
            var lods = new List<MorphTargetLod>();
            foreach (var lodIndex in lodIndices)
            {
                var basePositions = corpus.BaseLods[lodIndex];
                if (corpus.Samples.Any(face => lodIndex >= face.BakedLods.Count ||
                                               face.BakedLods[lodIndex].Length != basePositions.Length))
                {
                    throw new InvalidDataException($"Vorcha oracle LOD {lodIndex} is not compatible with its base mesh.");
                }
                var vertices = new MorphVertexDelta[basePositions.Length];
                for (var vertex = 0; vertex < basePositions.Length; vertex++)
                {
                    var delta = Vector3.Zero;
                    for (var sample = 0; sample < corpus.Samples.Count; sample++)
                    {
                        delta += (corpus.Samples[sample].BakedLods[lodIndex][vertex] - basePositions[vertex]) *
                                 (float)pseudoInverse[featureIndex, sample];
                    }
                    vertices[vertex] = new MorphVertexDelta(vertex, delta, Vector3.Zero);
                }
                lods.Add(new MorphTargetLod(lodIndex, basePositions.Length, vertices));
            }

            var boneOffsets = corpus.BaseBones
                .OrderBy(value => value.Key, StringComparer.OrdinalIgnoreCase)
                .Select(baseBone =>
                {
                    var offset = Vector3.Zero;
                    for (var sample = 0; sample < corpus.Samples.Count; sample++)
                    {
                        var baked = corpus.Samples[sample].Bones.GetValueOrDefault(baseBone.Key, baseBone.Value);
                        offset += (baked - baseBone.Value) * (float)pseudoInverse[featureIndex, sample];
                    }
                    return new MorphTargetBoneOffset(baseBone.Key, offset);
                })
                .ToArray();
            targets.Add(new MorphTargetAsset(
                new AssetIdentity(
                    "generated://vorcha-le2-reconstruction",
                    $"VorchaReconstruction.{featureName}",
                    featureIndex + 1,
                    "MorphTarget"),
                lods,
                boneOffsets));
        }
        return targets;
    }

    private static (float Position, float Bone) VerifyLe2Oracles(
        Corpus corpus,
        IReadOnlyList<string> featureNames,
        IReadOnlyList<MorphTargetAsset> targets)
    {
        var targetByName = targets.ToDictionary(target => target.Source.InstancedPath.Split('.').Last(), StringComparer.OrdinalIgnoreCase);
        var maximumPosition = 0f;
        var maximumBone = 0f;
        foreach (var sample in corpus.Samples)
        {
            foreach (var lod in targets[0].Lods.Select(value => value.LodIndex))
            {
                for (var vertex = 0; vertex < corpus.BaseLods[lod].Length; vertex++)
                {
                    var rebuilt = corpus.BaseLods[lod][vertex];
                    foreach (var feature in featureNames)
                    {
                        var delta = targetByName[feature].Lods.Single(value => value.LodIndex == lod).Vertices[vertex].PositionDelta;
                        rebuilt += delta * sample.Features[feature];
                    }
                    maximumPosition = Math.Max(maximumPosition, Vector3.Distance(rebuilt, sample.BakedLods[lod][vertex]));
                }
            }
            foreach (var baseBone in corpus.BaseBones)
            {
                var rebuilt = baseBone.Value;
                foreach (var feature in featureNames)
                {
                    var offset = targetByName[feature].BoneOffsets.Single(value => value.BoneName.Equals(
                        baseBone.Key, StringComparison.OrdinalIgnoreCase)).Offset;
                    rebuilt += offset * sample.Features[feature];
                }
                maximumBone = Math.Max(maximumBone, Vector3.Distance(
                    rebuilt,
                    sample.Bones.GetValueOrDefault(baseBone.Key, baseBone.Value)));
            }
        }
        return (maximumPosition, maximumBone);
    }

    private static int[] CompatibleOracleLods(Corpus corpus) => corpus.BaseLods
        .Select((positions, index) => (Positions: positions, Index: index))
        .Where(value => corpus.Samples.All(face => value.Index < face.BakedLods.Count &&
                                            face.BakedLods[value.Index].Length == value.Positions.Length))
        .Select(value => value.Index)
        .ToArray();

    private static double[,] MinimumNormPseudoInverse(double[,] weights)
    {
        var samples = weights.GetLength(0);
        var features = weights.GetLength(1);
        var gram = new double[samples, samples];
        for (var row = 0; row < samples; row++)
        {
            for (var column = 0; column < samples; column++)
            {
                for (var feature = 0; feature < features; feature++)
                {
                    gram[row, column] += weights[row, feature] * weights[column, feature];
                }
            }
        }
        var inverse = Invert(gram);
        var result = new double[features, samples];
        for (var feature = 0; feature < features; feature++)
        {
            for (var sample = 0; sample < samples; sample++)
            {
                for (var row = 0; row < samples; row++)
                {
                    result[feature, sample] += weights[row, feature] * inverse[row, sample];
                }
            }
        }
        return result;
    }

    private static double[,] Invert(double[,] matrix)
    {
        var size = matrix.GetLength(0);
        var augmented = new double[size, size * 2];
        for (var row = 0; row < size; row++)
        {
            for (var column = 0; column < size; column++) augmented[row, column] = matrix[row, column];
            augmented[row, size + row] = 1;
        }
        for (var column = 0; column < size; column++)
        {
            var pivot = Enumerable.Range(column, size - column)
                .OrderByDescending(row => Math.Abs(augmented[row, column]))
                .First();
            if (Math.Abs(augmented[pivot, column]) < 1e-12)
            {
                throw new InvalidDataException("Vorcha feature observations are rank deficient.");
            }
            if (pivot != column)
            {
                for (var index = 0; index < size * 2; index++)
                {
                    (augmented[column, index], augmented[pivot, index]) = (augmented[pivot, index], augmented[column, index]);
                }
            }
            var scale = augmented[column, column];
            for (var index = 0; index < size * 2; index++) augmented[column, index] /= scale;
            for (var row = 0; row < size; row++)
            {
                if (row == column) continue;
                var factor = augmented[row, column];
                for (var index = 0; index < size * 2; index++) augmented[row, index] -= factor * augmented[column, index];
            }
        }
        var result = new double[size, size];
        for (var row = 0; row < size; row++)
        {
            for (var column = 0; column < size; column++) result[row, column] = augmented[row, size + column];
        }
        return result;
    }

    private static Vector3 ReadVector(StructProperty? value) => value is null
        ? Vector3.Zero
        : new Vector3(
            value.GetProp<FloatProperty>("X")?.Value ?? 0,
            value.GetProp<FloatProperty>("Y")?.Value ?? 0,
            value.GetProp<FloatProperty>("Z")?.Value ?? 0);

    private static float MaximumDifference(IReadOnlyList<Vector3> left, IReadOnlyList<Vector3> right)
    {
        if (left.Count != right.Count) return float.PositiveInfinity;
        var maximum = 0f;
        for (var index = 0; index < left.Count; index++) maximum = Math.Max(maximum, Vector3.Distance(left[index], right[index]));
        return maximum;
    }

    private sealed record Corpus(
        IReadOnlyList<Vector3[]> BaseLods,
        IReadOnlyDictionary<string, Vector3> BaseBones,
        IReadOnlyList<CorpusFace> Samples);

    private sealed record CorpusFace(
        string Path,
        ExportEntry BaseHead,
        IReadOnlyDictionary<string, float> Features,
        IReadOnlyDictionary<string, Vector3> Bones,
        IReadOnlyList<Vector3[]> BakedLods);
}
