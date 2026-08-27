using System.Diagnostics;
using System.Numerics;
using MorphFaceEditor.Core.Deformation;
using MorphFaceEditor.Core.Domain;

namespace MorphFaceEditor.Benchmarks;

public static class SparseMorphBenchmark
{
    public static void Run()
    {
        const int vertexCount = 20_000;
        const int targetCount = 64;
        const int deltasPerTarget = 750;
        const int iterations = 100;

        var identity = new AssetIdentity("synthetic", "Synthetic", 1, "SkeletalMesh");
        var topology = new SkeletalMeshTopology(0, vertexCount, 0, 0, [], [], [], [], [], "synthetic");
        var mesh = new SkeletalMeshAsset(
            identity,
            Enumerable.Range(0, vertexCount).Select(index => new Vector3(index * 0.001f)).ToArray(),
            Enumerable.Repeat(Vector3.UnitZ, vertexCount).ToArray(),
            topology);
        var targets = CreateTargets(identity, vertexCount, targetCount, deltasPerTarget);

        for (var index = 0; index < 5; index++)
        {
            SparseMorphEvaluator.Evaluate(mesh, targets);
        }

        var timings = new double[iterations];
        for (var index = 0; index < iterations; index++)
        {
            var stopwatch = Stopwatch.StartNew();
            SparseMorphEvaluator.Evaluate(mesh, targets);
            stopwatch.Stop();
            timings[index] = stopwatch.Elapsed.TotalMilliseconds;
        }

        Console.WriteLine($"Sparse deformation: {vertexCount:N0} vertices, {targetCount} targets, {targetCount * deltasPerTarget:N0} deltas");
        Console.WriteLine($"Median: {BenchmarkStatistics.Percentile(timings, 0.50):F3} ms");
        Console.WriteLine($"p95: {BenchmarkStatistics.Percentile(timings, 0.95):F3} ms");
    }

    private static WeightedMorphTarget[] CreateTargets(
        AssetIdentity identity,
        int vertexCount,
        int targetCount,
        int deltasPerTarget)
    {
        var random = new Random(804);
        return Enumerable.Range(0, targetCount)
            .Select(targetIndex => new WeightedMorphTarget(
                $"Feature{targetIndex}",
                new MorphTargetAsset(
                    identity with { InstancedPath = $"Target{targetIndex}" },
                    [new MorphTargetLod(
                        0,
                        vertexCount,
                        Enumerable.Range(0, deltasPerTarget)
                            .Select(_ => new MorphVertexDelta(
                                random.Next(vertexCount),
                                new Vector3(0.001f, -0.002f, 0.003f),
                                new Vector3(0.0001f)))
                            .ToArray())],
                    []),
                0.5f))
            .ToArray();
    }
}
