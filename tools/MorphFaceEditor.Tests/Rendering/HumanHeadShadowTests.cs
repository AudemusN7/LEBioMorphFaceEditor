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
        new("HMF shadow bounds return exactly after extreme geometry is restored", HmfShadowBoundsReturnAfterGeometryRestore)
    ];

    private static string CorpusPath => Path.GetFullPath(Path.Combine(
        "fixtures", "Global Morphs", "LE2 GlobalMorphs.pcc"));

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
        var expanded = renderableMeshes.Select(mesh => new HeadPreviewMeshVertexUpdate(mesh.Name,
            mesh.Vertices.Select(vertex => vertex with
            {
                Position = center + (vertex.Position - center) * extremeScale
            }).ToArray())).ToArray();
        renderer.UpdateDeformation(new HeadPreviewDeformationUpdate(
            expanded[0].MeshName, expanded[0].Vertices, scene.SkinningPalette ?? [])
        {
            AttachmentUpdates = expanded[1..]
        });
        _ = renderer.Render(camera, options, animationTimeSeconds: 0f);
        var expandedRadius = ReadShadowRadius(renderer);
        TestAssert.True(expandedRadius > initialRadius * 2,
            $"The head deformation did not meaningfully expand shadow bounds (before={initialRadius:F5}, after={expandedRadius:F5}).");

        // This is the exact same vertex data the renderer received initially,
        // matching an edit followed by undo from the preview's point of view.
        renderer.UpdateDeformation(new HeadPreviewDeformationUpdate(
            renderableMeshes[0].Name, renderableMeshes[0].Vertices, scene.SkinningPalette ?? [])
        {
            AttachmentUpdates = renderableMeshes.Skip(1)
                .Select(mesh => new HeadPreviewMeshVertexUpdate(mesh.Name, mesh.Vertices)).ToArray()
        });
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


}
