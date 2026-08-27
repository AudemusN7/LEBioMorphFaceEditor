using System.Numerics;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Rendering;

namespace MorphFaceEditor.Benchmarks;

public static class HeadPreviewBenchmark
{
    public static void Run()
    {
        const int width = 1280;
        const int height = 720;
        const int iterations = 40;
        using var renderer = new HeadPreviewRenderer(width, height);
        var scene = CreateSphereScene(40, 64);
        renderer.SetScene(scene);
        var camera = new HeadOrbitCamera();
        camera.Fit(scene.Bounds);
        camera.ResetThreeQuarter();
        var pixels = new byte[width * height * 4];
        for (var index = 0; index < 5; index++)
        {
            renderer.Render(camera, new HeadPreviewOptions(), pixels);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var timings = new double[iterations];
        for (var index = 0; index < iterations; index++)
        {
            timings[index] = renderer.Render(camera, new HeadPreviewOptions(), pixels).RenderTime.TotalMilliseconds;
        }
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Console.WriteLine($"Head preview ({renderer.DeviceKind}): {width}×{height}, {scene.Meshes[0].Vertices.Count:N0} vertices, {scene.Meshes[0].Indices.Count / 3:N0} triangles");
        Console.WriteLine($"Median: {BenchmarkStatistics.Percentile(timings, 0.50):F3} ms");
        Console.WriteLine($"p95: {BenchmarkStatistics.Percentile(timings, 0.95):F3} ms");
        Console.WriteLine($"Managed allocation: {allocatedBytes / (double)iterations:N0} bytes/frame (caller-owned BGRA buffer)");
    }

    private static HeadPreviewScene CreateSphereScene(int latitudeSegments, int longitudeSegments)
    {
        var vertices = new List<HeadPreviewVertex>();
        for (var latitude = 0; latitude <= latitudeSegments; latitude++)
        {
            var v = latitude / (float)latitudeSegments;
            var phi = v * MathF.PI;
            for (var longitude = 0; longitude <= longitudeSegments; longitude++)
            {
                var u = longitude / (float)longitudeSegments;
                var theta = u * MathF.PI * 2;
                var normal = new Vector3(
                    MathF.Sin(phi) * MathF.Cos(theta),
                    MathF.Cos(phi),
                    MathF.Sin(phi) * MathF.Sin(theta));
                var position = normal * new Vector3(0.72f, 1f, 0.78f);
                vertices.Add(new HeadPreviewVertex(
                    position,
                    Vector3.Normalize(normal / new Vector3(0.72f, 1f, 0.78f)),
                    new Vector4(1, 0, 0, 1),
                    new Vector2(u, v),
                    0,
                    0,
                    0,
                    0,
                    new Vector4(1, 0, 0, 0)));
            }
        }

        var indices = new List<uint>();
        for (var latitude = 0; latitude < latitudeSegments; latitude++)
        {
            for (var longitude = 0; longitude < longitudeSegments; longitude++)
            {
                var first = latitude * (longitudeSegments + 1) + longitude;
                var second = first + longitudeSegments + 1;
                indices.AddRange([(uint)first, (uint)second, (uint)(first + 1)]);
                indices.AddRange([(uint)second, (uint)(second + 1), (uint)(first + 1)]);
            }
        }

        var sectionLength = indices.Count / 4;
        sectionLength -= sectionLength % 3;
        var families = new[] { HeadMaterialFamily.Skin, HeadMaterialFamily.Eyes, HeadMaterialFamily.Teeth, HeadMaterialFamily.Scalp };
        var sections = families.Select((family, index) =>
        {
            var baseIndex = index * sectionLength;
            var count = index == families.Length - 1 ? indices.Count - baseIndex : sectionLength;
            return new HeadPreviewSection(baseIndex, count, index, new HeadPreviewMaterial(family.ToString(), family));
        }).ToArray();
        var mesh = new HeadPreviewMesh("Synthetic head", vertices, indices, sections);
        return new HeadPreviewScene(
            "Synthetic head",
            [mesh],
            new HeadPreviewBounds(new Vector3(-0.72f, -1, -0.78f), new Vector3(0.72f, 1, 0.78f)));
    }
}
