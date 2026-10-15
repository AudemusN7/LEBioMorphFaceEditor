using MorphFaceEditor.Core.Services;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Rendering;
using MorphFaceEditor.Services;
using SharpDX.Direct3D11;
using Buffer = SharpDX.Direct3D11.Buffer;
using D3DDevice = SharpDX.Direct3D11.Device;
using D3DMapFlags = SharpDX.Direct3D11.MapFlags;

namespace MorphFaceEditor.Tests;

/// <summary>Shadow bounds and resolution regressions exercised on a corpus HMF head.</summary>
public static class HumanHeadShadowTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("HMF shadow bounds return exactly after extreme geometry is restored", HmfShadowBoundsReturnAfterGeometryRestore),
        new("HMF shadows retain world softness at 512, 1024, and 2048 resolution", HmfShadowsRetainWorldSoftnessAcrossResolutions)
    ];

    private static string CorpusPath => Path.GetFullPath(Path.Combine(
        "tests", "Global Morphs", "LE2 GlobalMorphs.pcc"));

    private static void HmfShadowBoundsReturnAfterGeometryRestore()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        using var loader = CreateLoader();
        var preview = LoadHmf(loader);
        var scene = preview.Scene;
        var renderableMeshes = scene.Meshes.Where(mesh => mesh.Vertices.Count > 0 && mesh.Indices.Count > 0).ToArray();
        var camera = new HeadOrbitCamera();
        camera.Fit(scene.Bounds);
        camera.Orbit(0.45f, -0.18f);
        using var renderer = new HeadPreviewRenderer(160, 160);
        renderer.SetScene(scene);

        var options = new HeadPreviewOptions(
            AmbientOcclusionEnabled: false,
            ShadowsEnabled: true,
            PostProcessingEnabled: false);
        var initial = renderer.Render(camera, options, animationTimeSeconds: 0f);
        TestAssert.True(initial.ShadowDrawCalls > 0, "The corpus HMF did not submit any casters to its shadow pass.");
        var initialPixels = initial.BgraPixels.ToArray();
        var initialRadius = ReadShadowRadius(renderer);

        const float extremeScale = 4f;
        var center = scene.Bounds.Center;
        foreach (var mesh in renderableMeshes)
        {
            var expanded = mesh.Vertices.Select(vertex => vertex with
            {
                Position = center + (vertex.Position - center) * extremeScale
            }).ToArray();
            renderer.UpdateMeshVertices(mesh.Name, expanded);
        }
        _ = renderer.Render(camera, options, animationTimeSeconds: 0f);
        var expandedRadius = ReadShadowRadius(renderer);
        TestAssert.True(expandedRadius > initialRadius * 2,
            $"The head deformation did not meaningfully expand shadow bounds (before={initialRadius:F5}, after={expandedRadius:F5}).");

        // This is the exact same vertex data the renderer received initially,
        // matching an edit followed by undo from the preview's point of view.
        foreach (var mesh in renderableMeshes)
        {
            renderer.UpdateMeshVertices(mesh.Name, mesh.Vertices);
        }
        var restored = renderer.Render(camera, options, animationTimeSeconds: 0f);
        var restoredRadius = ReadShadowRadius(renderer);
        TestAssert.True(restored.ShadowDrawCalls > 0, "Restoring the HMF geometry disabled its shadow casters.");
        TestAssert.Near(initialRadius, restoredRadius, 1e-6f);
        TestAssert.True(initialPixels.SequenceEqual(restored.BgraPixels),
            "The HMF shadowed frame changed after restoring the exact original vertex data.");

        if (renderableMeshes.Any(mesh => mesh.IsAttachment))
        {
            var hidden = renderer.Render(camera, options with { ShowAttachment = false }, animationTimeSeconds: 0f);
            var hiddenRadius = ReadShadowRadius(renderer);
            TestAssert.True(hiddenRadius <= restoredRadius + 1e-6f,
                $"Hiding HMF attachments expanded shadow bounds (visible={restoredRadius:F5}, hidden={hiddenRadius:F5}).");

            var shownAgain = renderer.Render(camera, options, animationTimeSeconds: 0f);
            TestAssert.Near(restoredRadius, ReadShadowRadius(renderer), 1e-6f);
            TestAssert.True(restored.BgraPixels.SequenceEqual(shownAgain.BgraPixels),
                "HMF shadowed output changed after hiding then restoring its attachments.");
        }
    }

    private static void HmfShadowsRetainWorldSoftnessAcrossResolutions()
    {
        LegendaryExplorerCoreRuntime.Initialize();
        using var loader = CreateLoader();
        var preview = LoadHmf(loader);
        var scene = preview.Scene;
        var camera = new HeadOrbitCamera();
        camera.Fit(scene.Bounds);
        camera.Orbit(0.45f, -0.18f);
        using var renderer = new HeadPreviewRenderer(160, 160);
        renderer.SetScene(scene);

        const float softnessWorld = 0.085f;
        byte[]? initial2048Pixels = null;
        var initial2048Casters = 0;
        foreach (var resolution in new[] { 2048, 512, 1024, 2048 })
        {
            var frame = renderer.Render(camera, new HeadPreviewOptions(
                AmbientOcclusionEnabled: false,
                ShadowsEnabled: true,
                ShadowMapResolution: resolution,
                ShadowSoftnessWorld: softnessWorld,
                PostProcessingEnabled: false), animationTimeSeconds: 0f);

            TestAssert.Equal(resolution, ReadShadowMapSize(renderer));
            TestAssert.True(frame.ShadowDrawCalls > 0,
                $"The HMF shadow pass had no casters at {resolution}x{resolution}.");
            TestAssert.True(frame.BgraPixels.Length == frame.Width * frame.Height * 4,
                $"The HMF preview returned incomplete output at {resolution}x{resolution} shadow resolution.");

            var filterParameters = ReadShadowFilterParameters(renderer);
            var representedSoftnessWorld = filterParameters.X * 2 * ReadShadowRadius(renderer);
            TestAssert.Near(softnessWorld, representedSoftnessWorld, 1e-5f);

            if (resolution == 512)
            {
                foreach (var boundarySoftness in new[] { 0f, 1f })
                {
                    var boundaryFrame = renderer.Render(camera, new HeadPreviewOptions(
                        AmbientOcclusionEnabled: false,
                        ShadowsEnabled: true,
                        ShadowMapResolution: resolution,
                        ShadowSoftnessWorld: boundarySoftness,
                        PostProcessingEnabled: false), animationTimeSeconds: 0f);
                    var boundaryParameters = ReadShadowFilterParameters(renderer);
                    var boundaryWorldRadius = boundaryParameters.X * 2 * ReadShadowRadius(renderer);
                    TestAssert.Near(boundarySoftness, boundaryWorldRadius, 1e-5f);
                    TestAssert.True(boundaryFrame.ShadowDrawCalls > 0,
                        $"The HMF shadow pass lost its casters at softness {boundarySoftness:F2}.");
                }
            }

            if (resolution == 2048 && initial2048Pixels is null)
            {
                initial2048Pixels = frame.BgraPixels.ToArray();
                initial2048Casters = frame.ShadowDrawCalls;
            }
            else if (resolution == 2048)
            {
                TestAssert.Equal(initial2048Casters, frame.ShadowDrawCalls);
                TestAssert.True(initial2048Pixels!.SequenceEqual(frame.BgraPixels),
                    "Switching shadow-map resolutions and restoring 2048 changed the same HMF frame.");
            }
        }
    }

    private static MorphFacePreviewLoadService CreateLoader() => new(
        new HeadPreviewSceneFactory(),
        new MorphTargetCatalog(),
        MorphFaceProfileRegistry.CreateDefault(),
        new MorphFacePackageReader());

    private static MorphFacePreviewLoadResult LoadHmf(MorphFacePreviewLoadService loader)
    {
        var catalog = new MorphFaceCatalogService().ReadAsync(CorpusPath).GetAwaiter().GetResult();
        var face = catalog.Faces.FirstOrDefault(candidate =>
            candidate.ProfileKey.Equals("le2-human-female", StringComparison.OrdinalIgnoreCase));
        if (face is null)
        {
            throw new InvalidDataException("LE2 GlobalMorphs has no supported HMF face for the shadow regression.");
        }
        return loader.LoadAsync(CorpusPath, face.InstancedPath).GetAwaiter().GetResult();
    }

    private static float ReadShadowRadius(HeadPreviewRenderer renderer) =>
        (float)(typeof(HeadPreviewRenderer)
            .GetField("_shadowRadius", BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(renderer) ?? throw new InvalidOperationException("Renderer shadow bounds were not found."));

    private static int ReadShadowMapSize(HeadPreviewRenderer renderer) =>
        (int)(typeof(HeadPreviewRenderer)
            .GetField("_shadowMapSize", BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(renderer) ?? throw new InvalidOperationException("Renderer shadow-map size was not found."));

    private static Vector4 ReadShadowFilterParameters(HeadPreviewRenderer renderer)
    {
        const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        var rendererType = typeof(HeadPreviewRenderer);
        var source = (Buffer?)rendererType.GetField("_shadowBuffer", privateInstance)?.GetValue(renderer)
            ?? throw new InvalidOperationException("Renderer shadow constants were not found.");
        var device = (D3DDevice?)rendererType.GetField("_device", privateInstance)?.GetValue(renderer)
            ?? throw new InvalidOperationException("Renderer D3D device was not found.");
        var context = (DeviceContext?)rendererType.GetField("_context", privateInstance)?.GetValue(renderer)
            ?? throw new InvalidOperationException("Renderer D3D context was not found.");
        using var staging = new Buffer(device, new BufferDescription
        {
            SizeInBytes = source.Description.SizeInBytes,
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CpuAccessFlags = CpuAccessFlags.Read,
            OptionFlags = ResourceOptionFlags.None,
            StructureByteStride = 0
        });
        context.CopyResource(source, staging);
        var mapped = context.MapSubresource(staging, 0, MapMode.Read, D3DMapFlags.None);
        try
        {
            // ShadowConstants contains a 64-byte matrix and two 16-byte vectors.
            return Marshal.PtrToStructure<Vector4>(mapped.DataPointer + 80);
        }
        finally
        {
            context.UnmapSubresource(staging, 0);
        }
    }
}
